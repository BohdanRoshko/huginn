import XCTest
@testable import HuginnCore

final class MeetingNotesTests: XCTestCase {
    func testRenderStampsEachNonEmptyLine() {
        let started = Date(timeIntervalSince1970: 0)
        let text = MeetingNotes.render(startedAt: started, lines: [
            (seconds: 0, text: "agenda"),
            (seconds: 71, text: "  ask Priya about churn "),
            (seconds: 5, text: "   "),
        ])
        XCTAssertNotNil(text)
        let lines = text!.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        XCTAssertTrue(lines[0].hasPrefix("# Notes typed during the meeting of "))
        XCTAssertEqual(lines[1], "")
        XCTAssertEqual(lines[2], "[00:00:00] agenda")
        XCTAssertEqual(lines[3], "[00:01:11] ask Priya about churn")
        XCTAssertEqual(lines.count, 5, "one trailing newline, so one empty tail element")
    }

    func testRenderReturnsNilWhenNothingWasTyped() {
        XCTAssertNil(MeetingNotes.render(startedAt: Date(), lines: [(seconds: 3, text: "   ")]))
        XCTAssertNil(MeetingNotes.render(startedAt: Date(), lines: []))
    }

    func testStampRollsPastAnHour() {
        XCTAssertEqual(MeetingNotes.stamp(seconds: 3725), "[01:02:05]")
        XCTAssertEqual(MeetingNotes.stamp(seconds: -4), "[00:00:00]")
    }
}

final class LineStampsTests: XCTestCase {
    func testFirstCharacterCreatesTheFirstLine() {
        var s = LineStamps()
        s.textChanged("a", caretUTF16: 1, now: 5)
        XCTAssertEqual(s.stamps, [5])
    }

    func testEnterAtTheEndAppendsAStamp() {
        var s = LineStamps()
        s.textChanged("a", caretUTF16: 1, now: 5)
        s.textChanged("a\n", caretUTF16: 2, now: 9)
        XCTAssertEqual(s.stamps, [5, 9])
    }

    func testEnterInTheMiddleInsertsAfterTheCaretLine() {
        var s = LineStamps()
        s.textChanged("a\nb", caretUTF16: 3, now: 1)
        XCTAssertEqual(s.stamps, [1, 1])
        s.textChanged("a\n\nb", caretUTF16: 2, now: 7)
        XCTAssertEqual(s.stamps, [1, 7, 1])
    }

    func testJoiningLinesRemovesTheStampAfterTheCaret() {
        var s = LineStamps()
        s.textChanged("a\nb", caretUTF16: 3, now: 1)
        s.textChanged("ab", caretUTF16: 1, now: 9)
        XCTAssertEqual(s.stamps, [1])
    }

    func testSelectAllDeleteClearsEverything() {
        var s = LineStamps()
        s.textChanged("a\nb", caretUTF16: 3, now: 1)
        s.textChanged("", caretUTF16: 0, now: 9)
        XCTAssertEqual(s.stamps, [])
    }

    func testPasteOfThreeLinesStampsAllThree() {
        var s = LineStamps()
        s.textChanged("x\ny\nz", caretUTF16: 5, now: 4)
        XCTAssertEqual(s.stamps, [4, 4, 4])
    }

    func testZeroAllAndSnapshotSkipEmptyLines() {
        var s = LineStamps()
        s.textChanged("a\n\nb", caretUTF16: 4, now: 6)
        s.zeroAll()
        let snap = s.snapshot("a\n\nb", now: 99)
        XCTAssertEqual(snap.map { $0.seconds }, [0, 0])
        XCTAssertEqual(snap.map { $0.text }, ["a", "b"])
    }

    func testSnapshotFallsBackToNowForAnUnknownLine() {
        let s = LineStamps()
        let snap = s.snapshot("q", now: 42)
        XCTAssertEqual(snap.count, 1)
        XCTAssertEqual(snap.first?.seconds, 42)
    }
}

final class TranscriptNamingTests: XCTestCase {
    func testDefaultNameWhenUnnamed() {
        XCTAssertEqual(TranscriptNaming.fileName(stamp: "2026-09-04_142911", name: nil), "2026-09-04_142911_transcript.txt")
        XCTAssertEqual(TranscriptNaming.fileName(stamp: "S", name: "  "), "S_transcript.txt")
    }

    func testNamedTranscriptIsFileSafe() {
        XCTAssertEqual(TranscriptNaming.fileName(stamp: "S", name: " Release: out / capacity  "), "S_Release out capacity.txt")
    }

    func testLongNamesAreCapped() {
        let long = String(repeating: "a", count: 100)
        XCTAssertEqual(TranscriptNaming.safeFileName(long).count, 80)
    }

    func testOnlyPunctuationIsEmpty() {
        XCTAssertEqual(TranscriptNaming.safeFileName(" ... "), "")
    }
}
