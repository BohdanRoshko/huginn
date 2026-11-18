import XCTest
@testable import HuginnCore

/// The expected values here are not hand-reasoned — they were produced by
/// running the original C# TranscriptReconciler (Odin's
/// POST /api/meetings/deecho) over these exact inputs. This suite is what pins
/// the Swift port to that behaviour.
///
/// Fixtures are synthetic on purpose. Real transcripts are meeting content and
/// this repository is public.
final class TranscriptReconcilerTests: XCTestCase {

    /// Every branch of the algorithm in one transcript. Golden output from the
    /// C# implementation: removedEchoes=4, meLines=7, themLines=6.
    private let mixed = """
    [00:00:01] Them: So the migration is going to take about three weeks in total.
    [00:00:04] Me: So the migration is going to take about three weeks in total.
    [00:00:10] Them: Okay.
    [00:00:11] Me: Okay.
    [00:00:20] Them: The first thing we need to do is check the inventory counts before anything else.
    [00:00:24] Me: check the inventory counts
    [00:00:30] Me: What about receivables?
    [00:00:32] Them: Receivables are handled in the second phase.
    [00:00:40] Them: We should move all of that logic into the service layer eventually.
    [00:00:43] Me: Right, but the impact of moving that to code is not the same.
    [00:00:50] Them: I think the reporting piece is genuinely the hardest part of this whole thing.
    [00:00:54] Me: I think the reporting peace is generally the hardest part of this whole thing.
    [00:01:00] Me: Can you send me the spreadsheet afterwards?
    """

    func testMatchesGoldenOutputFromReferenceImplementation() {
        let result = TranscriptReconciler.deEcho(mixed)

        XCTAssertEqual(result.removedEchoes, 4)
        XCTAssertEqual(result.meLines, 7)
        XCTAssertEqual(result.themLines, 6)

        let expected = """
        [00:00:01] Them: So the migration is going to take about three weeks in total.
        [00:00:10] Them: Okay.
        [00:00:20] Them: The first thing we need to do is check the inventory counts before anything else.
        [00:00:30] Me: What about receivables?
        [00:00:32] Them: Receivables are handled in the second phase.
        [00:00:40] Them: We should move all of that logic into the service layer eventually.
        [00:00:43] Me: Right, but the impact of moving that to code is not the same.
        [00:00:50] Them: I think the reporting piece is genuinely the hardest part of this whole thing.
        [00:01:00] Me: Can you send me the spreadsheet afterwards?

        """
        XCTAssertEqual(result.text, expected)
    }

    // MARK: - the individual rules, so a regression names itself

    func testDropsVerbatimEcho() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:01] Them: So the migration is going to take about three weeks in total.
        [00:00:04] Me: So the migration is going to take about three weeks in total.
        """)
        XCTAssertEqual(r.removedEchoes, 1)
        XCTAssertFalse(r.text.contains("Me:"))
    }

    func testDropsShortBackchannelEcho() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:10] Them: Okay.
        [00:00:11] Me: Okay.
        """)
        XCTAssertEqual(r.removedEchoes, 1)
    }

    /// The mic re-chunks their sentence, so a few of their words stand alone as
    /// a "Me" line. A subset, never a near-equal, so the Jaccard rule cannot see it.
    func testDropsFragmentEchoWhenTheySpokeFirst() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:20] Them: The first thing we need to do is check the inventory counts before anything else.
        [00:00:24] Me: check the inventory counts
        """)
        XCTAssertEqual(r.removedEchoes, 1)
    }

    /// The load-bearing gate. Same words, but the user spoke FIRST — an echo
    /// cannot precede its source, so this is a genuine question and must survive.
    func testKeepsShortQuestionAskedBeforeTheirAnswer() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:30] Me: What about receivables?
        [00:00:32] Them: Receivables are handled in the second phase.
        """)
        XCTAssertEqual(r.removedEchoes, 0)
        XCTAssertTrue(r.text.contains("What about receivables?"))
    }

    /// The mic mangles exactly the distinctive words ("piece" -> "peace",
    /// "genuinely" -> "generally"), which would fail the content-word gate. The
    /// near-verbatim bypass is what catches it.
    func testDropsMangledEchoViaNearVerbatimBypass() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:50] Them: I think the reporting piece is genuinely the hardest part of this whole thing.
        [00:00:54] Me: I think the reporting peace is generally the hardest part of this whole thing.
        """)
        XCTAssertEqual(r.removedEchoes, 1)
    }

    /// A genuine interjection built largely from common words. A big union
    /// covers "but/the/of/that/to/not" by itself, so only the content-word gate
    /// keeps this alive.
    func testKeepsGenuineInterjectionWithDistinctContentWords() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:40] Them: We should move all of that logic into the service layer eventually.
        [00:00:43] Me: Right, but the impact of moving that to code is not the same.
        """)
        XCTAssertEqual(r.removedEchoes, 0)
    }

    // MARK: - shape and safety

    func testLeavesUnrelatedTranscriptUntouched() {
        let input = """
        [00:00:01] Them: How did the deployment go last night?
        [00:00:05] Me: Fine, no rollbacks needed.
        """
        let r = TranscriptReconciler.deEcho(input)
        XCTAssertEqual(r.removedEchoes, 0)
        // Unchanged input is returned verbatim, not re-serialised.
        XCTAssertEqual(r.text, input)
    }

    func testPreservesHeaderAndWarningLines() {
        let r = TranscriptReconciler.deEcho("""
        Recorded Aug 5, 2026 at 2:30 PM

        !! The other side of the call was NOT captured.

        [00:00:01] Them: One two three four five six.
        [00:00:03] Me: One two three four five six.
        """)
        XCTAssertEqual(r.removedEchoes, 1)
        XCTAssertTrue(r.text.contains("Recorded Aug 5, 2026 at 2:30 PM"))
        XCTAssertTrue(r.text.contains("!! The other side of the call was NOT captured."))
    }

    func testNonTranscriptTextIsReturnedUnchanged() {
        let input = "just some notes\nnot a transcript at all"
        let r = TranscriptReconciler.deEcho(input)
        XCTAssertEqual(r.text, input)
        XCTAssertEqual(r.removedEchoes, 0)
        XCTAssertEqual(r.meLines, 0)
        XCTAssertEqual(r.themLines, 0)
    }

    func testEmptyInput() {
        let r = TranscriptReconciler.deEcho("")
        XCTAssertEqual(r.removedEchoes, 0)
        XCTAssertEqual(r.text, "")
    }

    /// An echo can arrive well after its source because the two tracks chunk
    /// the same speech differently — this is timestamp drift, not acoustic delay.
    func testEchoOutsideTheWindowSurvives() {
        let r = TranscriptReconciler.deEcho("""
        [00:00:01] Them: So the migration is going to take about three weeks in total.
        [00:00:45] Me: So the migration is going to take about three weeks in total.
        """)
        XCTAssertEqual(r.removedEchoes, 0)
    }

    func testTokenizeDropsPunctuationAndSingleCharacters() {
        XCTAssertEqual(TranscriptReconciler.tokenize("Consciable AR."), ["consciable", "ar"])
        // "it's" splits to "it" + "s"; the stray single character is dropped.
        XCTAssertEqual(TranscriptReconciler.tokenize("it's fine"), ["it", "fine"])
    }
}
