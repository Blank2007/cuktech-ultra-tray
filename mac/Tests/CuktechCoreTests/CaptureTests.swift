import XCTest
@testable import CuktechCore

final class CaptureTests: XCTestCase {
    private func sample(_ v: Double, _ a: Double, state: UInt8 = 0x01) -> PortData {
        PortData(state: state, code: 0, current: a, voltage: v)
    }

    /// 20V × 3A 持续 60 秒 → 正好 1Wh（20*3=60W，60W×1h/60 = 1Wh）。
    func testSteadyLoad() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...60 {
            acc.add(sample(20.0, 3.0), at: t0.addingTimeInterval(Double(i)))
        }
        XCTAssertEqual(acc.energyWh, 1.0, accuracy: 0.02)
        XCTAssertEqual(acc.averagePower, 60.0, accuracy: 1.0)
        XCTAssertEqual(acc.effectiveSeconds, 60.0, accuracy: 0.01)
        XCTAssertEqual(acc.voltageChanges, 0)
        XCTAssertEqual(acc.sampleCount, 61)
        XCTAssertEqual(acc.averageVoltage, 20.0, accuracy: 0.01)
        XCTAssertEqual(acc.averageCurrent, 3.0, accuracy: 0.01)
        XCTAssertEqual(acc.peakCurrent, 3.0, accuracy: 0.01)
    }

    /// 5V×2A 充 10 秒后切到 20V×2A 再充 10 秒。
    /// 采样点 0...10 是 5V 段、11...20 是 20V 段：10 个 5V 间隔 + 1 个跨档间隔 + 9 个 20V 间隔。
    /// 跨档那 1 秒按跳变前的 10W 记（保守，少算 30W·1s ≈ 0.008Wh），
    /// 所以是 (10×10 + 10×1 + 40×9)/3600 = 0.1306Wh。
    /// 关键是不能把它算成梯形——那样跨档那秒会按 25W，结果比 0.14 还高。
    func testVoltageStepIsNotAveraged() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...10 {
            acc.add(sample(5.0, 2.0), at: t0.addingTimeInterval(Double(i)))
        }
        for i in 11...20 {
            acc.add(sample(20.0, 2.0), at: t0.addingTimeInterval(Double(i)))
        }
        XCTAssertEqual(acc.energyWh, 0.1306, accuracy: 0.002)
        XCTAssertEqual(acc.voltageChanges, 1)
        XCTAssertEqual(acc.minVoltage ?? 0, 5.0, accuracy: 0.001)
        XCTAssertEqual(acc.maxVoltage ?? 0, 20.0, accuracy: 0.001)
        // 平均电压也走分段口径：(5×10 + 5×1 + 20×9)/20 = 235/20 = 11.75V。
        // 要是按两端梯形平均，跨档那秒会记成 12.5V，结果会偏到 12.125。
        XCTAssertEqual(acc.averageVoltage, 11.75, accuracy: 0.01)
        XCTAssertEqual(acc.averageCurrent, 2.0, accuracy: 0.01)
        XCTAssertEqual(acc.peakCurrent, 2.0, accuracy: 0.001)
    }

    /// 拔线不是换档：电压从 9V 掉到 0V 差值够大，但设备并没有切档位。
    func testUnplugIsNotARailChange() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...10 { acc.add(sample(9.0, 2.0), at: t0.addingTimeInterval(Double(i))) }
        for i in 11...40 { acc.add(sample(0, 0, state: 0x00), at: t0.addingTimeInterval(Double(i))) }
        XCTAssertEqual(acc.voltageChanges, 0)
        // 空载的 0V 也不能污染电压区间
        XCTAssertEqual(acc.minVoltage ?? 0, 9.0, accuracy: 0.001)
        XCTAssertEqual(acc.maxVoltage ?? 0, 9.0, accuracy: 0.001)
        XCTAssertEqual(acc.peakCurrent, 2.0, accuracy: 0.001)
    }

    /// 空载的秒不参与积分，也不算有效时长。
    func testIdleDoesNotCount() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...10 { acc.add(sample(9.0, 2.0), at: t0.addingTimeInterval(Double(i))) }
        for i in 11...50 { acc.add(sample(0, 0, state: 0x00), at: t0.addingTimeInterval(Double(i))) }
        XCTAssertEqual(acc.energyWh, 0.05, accuracy: 0.005)   // 18W*10s/3600
        XCTAssertEqual(acc.effectiveSeconds, 10.0, accuracy: 0.01)
        XCTAssertEqual(acc.averagePower, 18.0, accuracy: 0.5)
        XCTAssertEqual(acc.peakPower, 18.0, accuracy: 0.01)
    }

    /// 中间断了一大截（重连），这段不能凭空算电量。
    func testGapIsSkipped() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        acc.add(sample(20.0, 3.0), at: t0)
        acc.add(sample(20.0, 3.0), at: t0.addingTimeInterval(120))  // 隔了 2 分钟
        XCTAssertEqual(acc.energyWh, 0, accuracy: 0.0001)
        XCTAssertEqual(acc.effectiveSeconds, 0, accuracy: 0.0001)
    }

    /// 0.1V 的读数抖动不该当成换档。
    func testSmallJitterIsSameRail() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        acc.add(sample(20.0, 3.0), at: t0)
        acc.add(sample(20.1, 3.0), at: t0.addingTimeInterval(1))
        XCTAssertEqual(acc.voltageChanges, 0)
        XCTAssertEqual(acc.energyWh, 60.15 / 3600, accuracy: 0.0005)
    }

    func testResultFormatting() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...72 { acc.add(sample(9.0, 2.0), at: t0.addingTimeInterval(Double(i))) }
        let r = CaptureResult(portIndex: 1, startedAt: t0,
                              endedAt: t0.addingTimeInterval(102),
                              stats: acc, reason: .noLoad, firmware: "2.1.2_0079")
        XCTAssertEqual(r.portName, "C1")
        XCTAssertEqual(r.energyText, "0.36")
        XCTAssertEqual(r.averageText, "18.0")
        XCTAssertEqual(r.averageVoltageText, "9.00")
        XCTAssertEqual(r.averageCurrentText, "2.00")
        XCTAssertEqual(r.peakCurrentText, "2.00")
        XCTAssertFalse(r.spansRails)
        XCTAssertTrue(r.summaryText.contains("平均电压：9.00 V（最大 9.00 V）"))
        XCTAssertTrue(r.summaryText.contains("平均电流：2.00 A（最大 2.00 A）"))
        XCTAssertEqual(CaptureResult.duration(72), "1 分 12 秒")
        XCTAssertEqual(CaptureResult.duration(45), "45 秒")
        XCTAssertEqual(CaptureResult.duration(3720), "1 小时 02 分")
        XCTAssertTrue(r.summaryText.contains("充入电量：0.36 Wh"))
    }

    /// 导出的 CSV：中文表头、定点小数、行尾 CRLF，空载那秒也要有一行（live=0）。
    /// 用 CRLF 而不是 LF：Windows 版就是这么写的，两个平台的导出文件才能直接 diff。
    func testCsvExport() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...2 { acc.add(sample(9.0, 2.0), at: t0.addingTimeInterval(Double(i))) }
        acc.add(sample(0, 0, state: 0x00), at: t0.addingTimeInterval(3))   // 拔掉
        let r = CaptureResult(portIndex: 1, startedAt: t0, endedAt: t0.addingTimeInterval(4),
                              stats: acc, reason: .noLoad, firmware: "2.1.2_0079")

        XCTAssertEqual(acc.samples.count, 4)
        let lines = acc.samples.map { $0.t }
        XCTAssertEqual(lines, [0, 1, 2, 3])
        XCTAssertTrue(acc.samples[3].live == false)

        let csv = r.csvText()
        let rows = csv.components(separatedBy: "\r\n")
        XCTAssertEqual(rows.first, "时间(s),电压(V),电流(A),功率(W),带载")
        XCTAssertEqual(rows.count, 6)   // 表头 + 4 行 + 结尾那个空串
        XCTAssertEqual(rows[1], "0.0,9.00,2.000,18.00,1")
        XCTAssertEqual(rows[4], "3.0,0.00,0.000,0.00,0")

        XCTAssertTrue(r.defaultFileName.hasPrefix("cuktech-capture-C1-"))
        XCTAssertTrue(r.defaultFileName.hasSuffix(".csv"))
    }

    /// 采样点有上限（约 2 小时）。超了就不再记，但统计数字照常累计，导出也不会撑爆。
    func testSampleCap() {
        var acc = CaptureAccumulator()
        let t0 = Date()
        for i in 0...(CaptureAccumulator.sampleCap + 10) {
            acc.add(sample(9.0, 2.0), at: t0.addingTimeInterval(Double(i)))
        }
        XCTAssertEqual(acc.samples.count, CaptureAccumulator.sampleCap)
        XCTAssertGreaterThan(acc.energyWh, 0)
    }
}
