<!-- snippet: publishing-decider-tests -->
```cs
public class EditorialTests
{
    private static readonly SectionRef Intro = new("intro", "Introduction", "h1");

    [Fact]
    public void Submitting_freezes_the_draft_and_pins_the_round_to_that_version() =>
        Decider.Given<Manuscript>(new ManuscriptStarted("T"), new SectionRevised("intro", "Introduction", "h1"))
            .When(Editorial.Submit)
            .Then(new VersionFrozen(1, Stage.SubmittedUnderReview, [Intro], null, null), new ReviewRoundOpened(1, 1));

    [Fact]
    public void Authors_cannot_change_a_version_that_reviewers_read() =>
        Decider.Given<Manuscript>(new ManuscriptStarted("T"), new SectionRevised("intro", "Introduction", "h1"),
                new VersionFrozen(1, Stage.SubmittedUnderReview, [Intro], null, null), new ReviewRoundOpened(1, 1))
            .When(m => Editorial.Revise(m, "intro", "Introduction", "h2"))
            .ThenThrows<InvalidOperationException>();

    [Fact]
    public void Acceptance_freezes_the_reviewed_content_as_the_accepted_manuscript() =>
        Decider.Given<Manuscript>(new ManuscriptStarted("T"), new SectionRevised("intro", "Introduction", "h1"),
                new VersionFrozen(1, Stage.SubmittedUnderReview, [Intro], null, null), new ReviewRoundOpened(1, 1))
            .When(m => Editorial.Decide(m, Decision.Accept))
            .Then(new DecisionMade(1, Decision.Accept), new VersionFrozen(2, Stage.AcceptedManuscript, [Intro], 1, null));
}
```
<!-- endSnippet -->
