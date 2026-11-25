import XCTest
@testable import CuktechCore

final class ModelsTests: XCTestCase {
    func testWattBuckets() {
        XCTAssertEqual(WattStyle.port(0), WattStyle.dim)
        XCTAssertEqual(WattStyle.port(0.04), WattStyle.dim)
        XCTAssertEqual(WattStyle.port(0.1), WattStyle.cool)
        XCTAssertEqual(WattStyle.port(20), WattStyle.ok)
        XCTAssertEqual(WattStyle.port(40), WattStyle.warn)
        XCTAssertEqual(WattStyle.port(80), WattStyle.bad)
    }

    func testTotalBuckets() {
        XCTAssertEqual(WattStyle.total(0), WattStyle.dim)
        XCTAssertEqual(WattStyle.total(6.0), WattStyle.cool)
        XCTAssertEqual(WattStyle.total(20), WattStyle.ok)
        XCTAssertEqual(WattStyle.total(60), WattStyle.warn)
        XCTAssertEqual(WattStyle.total(100), WattStyle.bad)
    }

    func testWattTextFormat() {
        XCTAssertEqual(WattStyle.text(6.0), "6.0W")
        XCTAssertEqual(WattStyle.text(99.9), "99.9W")
        XCTAssertEqual(WattStyle.text(120.0), "120W")
    }

    func testSnapshotSumsTotal() {
        var snap = DeviceSnapshot()
        snap.connected = true
        snap.ports[0].apply(PortData(state: 0x01, code: 0x0A, current: 2.0, voltage: 20.1))
        snap.ports[2].apply(PortData(state: 0x01, code: 0x05, current: 0.5, voltage: 5.0))
        XCTAssertEqual(snap.totalWatts, 42.7, accuracy: 0.001)
        XCTAssertEqual(snap.totalText, "42.7W")
        XCTAssertTrue(snap.headline.contains("已连接"))
        XCTAssertTrue(snap.headline.contains("总输出 42.7W"))
    }

    func testIdlePortDetail() {
        var slot = PortSlot(index: 1)
        XCTAssertEqual(slot.detail, "无应答")
        slot.apply(PortData(state: 0x00, code: 0, current: 0, voltage: 0))
        XCTAssertEqual(slot.detail, "空载")
        slot.enabled = false
        XCTAssertEqual(slot.detail, "已断电")
    }

    func testSceneNames() {
        XCTAssertEqual(SceneMode.name(1), "AI")
        XCTAssertEqual(SceneMode.name(3), "单口")
        XCTAssertEqual(SceneMode.name(9), "模式 9")
    }

    func testPortSlotZeroBasedIndex() {
        let slots = (1...4).map { PortSlot(index: $0) }
        XCTAssertEqual(slots.map(\.zeroBasedIndex), [0, 1, 2, 3])
    }

    // MARK: - C3 与 A 合并成一路功率计（issue #1，报告人 KingGxxT）

    /// 实测样例：port_switch=0x0F 时 C3 回 11300433（state=0x11、0.4A、5.1V），
    /// A 回 00300000（state=0x00、code=0x30 = 通电但空载）。合并时 A 那一行不该写「空载」。
    func testMergedPairC3OwnsTheReading() {
        var snap = DeviceSnapshot()
        snap.ports[2].apply(PortData(state: 0x11, code: 0x04, current: 0.4, voltage: 5.1))
        snap.ports[3].apply(PortData(state: 0x00, code: 0x30, current: 0, voltage: 0))

        XCTAssertTrue(snap.pairMerged)
        XCTAssertEqual(snap.mergedOwner, 3)
        // 0.4A × 5.1V = 2.04W：这一对只算一次，不能把两路加起来
        XCTAssertEqual(snap.totalWatts, 2.04, accuracy: 0.001)
        XCTAssertEqual(snap.totalText, "2.0W")

        XCTAssertEqual(snap.rowName(snap.ports[2]), "C3+A")
        XCTAssertEqual(snap.rowName(snap.ports[3]), "A")
        XCTAssertEqual(snap.rowDetail(snap.ports[3]), "并入 C3+A")
        XCTAssertEqual(snap.rowWattsText(snap.ports[3]), "—")
        XCTAssertTrue(snap.isMergedPassenger(snap.ports[3]))
        XCTAssertFalse(snap.isMergedPassenger(snap.ports[2]))
        // C3 自己是 owner，读数照旧
        XCTAssertEqual(snap.rowDetail(snap.ports[2]), "5.1V · 0.40A")
    }

    /// A 是 0x11 那一侧时 owner 换成 A；两路都有数也只累加一份。
    func testMergedPairAOwnsWhenC3Idle() {
        var snap = DeviceSnapshot()
        snap.ports[2].apply(PortData(state: 0x00, code: 0x10, current: 0, voltage: 0))
        snap.ports[3].apply(PortData(state: 0x11, code: 0x30, current: 1.5, voltage: 9.0))

        XCTAssertTrue(snap.pairMerged)
        XCTAssertEqual(snap.mergedOwner, 4)
        XCTAssertEqual(snap.rowName(snap.ports[3]), "C3+A")
        XCTAssertEqual(snap.rowDetail(snap.ports[2]), "并入 C3+A")
        XCTAssertEqual(snap.totalWatts, 13.5, accuracy: 0.001)
    }

    /// 两个 0x11 都在时按规格取 C3 当 owner。
    func testMergedPairBothWorkingPrefersC3() {
        var snap = DeviceSnapshot()
        snap.ports[2].apply(PortData(state: 0x11, code: 0x04, current: 0.4, voltage: 5.1))
        snap.ports[3].apply(PortData(state: 0x11, code: 0x30, current: 0.4, voltage: 5.1))

        XCTAssertTrue(snap.pairMerged)
        XCTAssertEqual(snap.mergedOwner, 3)
        XCTAssertEqual(snap.totalWatts, 2.04, accuracy: 0.001)
        XCTAssertEqual(snap.rowDetail(snap.ports[3]), "并入 C3+A")
    }

    /// 没合并时行为完全不变：名字、读数、总功率都还是原来那套。
    func testNotMergedKeepsOriginalBehavior() {
        var snap = DeviceSnapshot()
        snap.ports[2].apply(PortData(state: 0x01, code: 0x04, current: 0.4, voltage: 5.1))
        snap.ports[3].apply(PortData(state: 0x00, code: 0x30, current: 0, voltage: 0))

        XCTAssertFalse(snap.pairMerged)
        XCTAssertEqual(snap.rowName(snap.ports[2]), "C3")
        XCTAssertEqual(snap.rowName(snap.ports[3]), "A")
        XCTAssertEqual(snap.rowDetail(snap.ports[3]), "空载")
        XCTAssertEqual(snap.rowWattsText(snap.ports[3]), "0.0W")
        XCTAssertFalse(snap.isMergedPassenger(snap.ports[3]))
        XCTAssertEqual(snap.totalWatts, 2.04, accuracy: 0.001)
    }

    /// 首帧还没来（没有 hasData）不算合并。
    func testNoDataIsNotMerged() {
        let snap = DeviceSnapshot()
        XCTAssertFalse(snap.pairMerged)
        XCTAssertEqual(snap.mergedOwner, 3)
        XCTAssertEqual(snap.rowDetail(snap.ports[2]), "无应答")
    }
}
