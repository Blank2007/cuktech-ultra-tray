import XCTest
@testable import CuktechCore

final class PowerHistoryTests: XCTestCase {
    private func snap(_ w: [Double], state: UInt8 = 0x01, connected: Bool = true) -> DeviceSnapshot {
        var s = DeviceSnapshot()
        s.connected = connected
        for (i, watts) in w.enumerated() where i < 4 {
            let v = watts > 0 ? 20.0 : 0.0
            let a = v > 0 ? watts / v : 0.0
            s.ports[i].apply(PortData(state: watts > 0 ? state : 0x00, code: 0, current: a, voltage: v))
        }
        return s
    }

    /// 两拍间隔不到 0.4 秒的丢一帧：ticker 是 400ms，偶尔会早到一点点，
    /// 不节流的话同一秒会塞进两个点，曲线横向被压歪。
    func testMinStepThrottles() {
        var h = PowerHistory()
        h.add(snap([10, 0, 0, 0]), at: 100.0)
        h.add(snap([20, 0, 0, 0]), at: 100.2)
        h.add(snap([30, 0, 0, 0]), at: 100.5)
        XCTAssertEqual(h.count, 2)
        let pts = h.snapshot(60, at: 101)
        XCTAssertEqual(pts.count, 2)
        XCTAssertEqual(pts[0].at(0), 10.0, accuracy: 0.001)
        XCTAssertEqual(pts[1].at(0), 30.0, accuracy: 0.001)
    }

    /// 时间倒流（设备重连、进程休眠后系统时间回拨）就整段清掉重来，
    /// 不能留一条横穿整个图的假线。
    func testClockGoingBackwardsClears() {
        var h = PowerHistory()
        h.add(snap([10, 0, 0, 0]), at: 100.0)
        h.add(snap([20, 0, 0, 0]), at: 99.0)
        XCTAssertEqual(h.count, 1)
        XCTAssertEqual(h.snapshot(60, at: 99).first?.at(0), 20.0)
    }

    /// 断连、无应答、合并时搭车的那一路都记 -1（= 断点），曲线在那里断开，
    /// 不会把「没数据」画成 0 W。
    func testBreaksAreNegative() {
        var h = PowerHistory()
        h.add(snap([0, 0, 0, 0]).withConnected(false), at: 100.0)
        h.add(snap([5, 0, 0, 0]), at: 101.0)
        var merged = snap([5, 0, 9, 0])
        merged.ports[2].apply(PortData(state: 0x11, code: 0, current: 0.45, voltage: 20))
        h.add(merged, at: 102.0)
        let pts = h.snapshot(60, at: 103)
        XCTAssertEqual(pts.count, 3)
        XCTAssertEqual(pts[0].at(0), -1)
        // 第三拍是 C3 带合并标记的那一路，A 是搭车的那一路
        XCTAssertEqual(pts[2].at(2), 9.0, accuracy: 0.001)
        XCTAssertEqual(pts[2].at(3), -1)
    }

    /// 容量上限：超了就丢最老的，内存不无限长。
    func testCapacityDropsOldest() {
        var h = PowerHistory()
        var t = 0.0
        for i in 0..<(PowerHistory.capacity + 50) {
            t += 1
            h.add(snap([Double(i), 0, 0, 0]), at: t)
        }
        XCTAssertEqual(h.count, PowerHistory.capacity)
        let pts = h.snapshot(100000, at: t)
        XCTAssertEqual(pts.first?.at(0), 50.0)
    }

    /// 窗口只取最近 N 秒。
    func testSnapshotWindow() {
        var h = PowerHistory()
        h.add(snap([1, 0, 0, 0]), at: 100.0)
        h.add(snap([2, 0, 0, 0]), at: 200.0)
        h.add(snap([3, 0, 0, 0]), at: 300.0)
        XCTAssertEqual(h.snapshot(120, at: 300).count, 2)
        XCTAssertEqual(h.snapshot(360, at: 300).count, 3)
    }

    /// 演示数据只用来截图，但也要讲得通：C1 一直在输出，
    /// solo 模式下第三路有值、第四路是断点。
    func testDemo() {
        let h = PowerHistory.demo(c3Only: false)
        // 180 秒的历史 = 首尾各一个点，共 181 个
        XCTAssertEqual(h.count, 181)
        let pts = h.snapshot(3600, at: PowerHistory.now)
        XCTAssertTrue(pts.contains { $0.at(0) > 20 })
        let solo = PowerHistory.demo(c3Only: true)
        XCTAssertTrue(solo.snapshot(3600, at: PowerHistory.now).allSatisfy { $0.at(0) == 0 && $0.at(2) > 0 })
    }
}

private extension DeviceSnapshot {
    /// 测试里偶尔要造「未连接」的一拍。
    func withConnected(_ on: Bool) -> DeviceSnapshot {
        var s = self
        s.connected = on
        return s
    }
}
