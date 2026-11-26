using System.Text.Json;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// Era questions ("Which cartoon did you grow up with?"): single choices whose options each point to a decade. They tell
/// the bots roughly when the room grew up, never an age, and the decade never leaves the server.
/// </summary>
public class EraQuestionTests
{
    private static readonly ContentPolicy Family = new(FamilyFriendly: true);
    private static readonly ContentPolicy Mature = new(FamilyFriendly: false);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static OnboardingQuestion Era(string id, params (string Text, string? Era)[] options) =>
        new(id, QuestionKind.SingleChoice, "Which cartoon did you grow up with?", false,
            options.Select((o, i) => new QuestionOption(((char)('a' + i)).ToString(), o.Text, false, o.Era)).ToArray(), 1, 1);

    private static OnboardingQuestion GoodEra(string id = "q1") =>
        Era(id, ("The Smurfs", "1980s"), ("Rugrats", "1990s"), ("SpongeBob", "2000s"), ("Bluey", "2020s"));

    [Fact]
    public void A_well_tagged_era_question_is_valid_and_counts_as_an_era_question_not_a_single_choice()
    {
        OnboardingQuestion era = GoodEra();
        Assert.True(era.IsEra);
        Assert.True(QuestionValidator.IsValid([era], Family, 1));
        Assert.True(QuestionValidator.IsValid([era], Family, new QuestionMix(0, 0, 0, 1)));
        Assert.False(QuestionValidator.IsValid([era], Family, new QuestionMix(0, 1, 0, 0)));
    }

    [Fact]
    public void A_badly_tagged_era_question_is_rejected()
    {
        // Only some options tagged.
        Assert.False(QuestionValidator.IsValid([Era("q1", ("A", "1980s"), ("B", "1990s"), ("C", "2000s"), ("D", null))], Family, 1));
        // Fewer than three different decades.
        Assert.False(QuestionValidator.IsValid([Era("q1", ("A", "1980s"), ("B", "1980s"), ("C", "2000s"), ("D", "2000s"))], Family, 1));
        // A decade that does not exist.
        Assert.False(QuestionValidator.IsValid([Era("q1", ("A", "1980s"), ("B", "1990s"), ("C", "the future"))], Family, 1));
        // An era question must be a single choice.
        OnboardingQuestion multi = GoodEra() with { Kind = QuestionKind.MultiChoice, MaxSelect = 2 };
        Assert.False(QuestionValidator.IsValid([multi], Family, 1));
        // It still may not ask for an age.
        Assert.False(QuestionValidator.IsValid([GoodEra() with { Text = "How old were you when Rugrats came out?" }], Family, 1));
    }

    [Fact]
    public void The_decade_never_reaches_a_client()
    {
        string json = JsonSerializer.Serialize(new OnboardingData([GoodEra()]), Web);
        Assert.DoesNotContain("era", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1990s", json);
        Assert.Contains("Rugrats", json);
    }

    [Fact]
    public void The_built_in_form_has_its_era_question_even_in_a_mature_room()
    {
        QuestionMix mix = new(1, 1, 1, 1);
        for (int i = 0; i < 40; i++)
        {
            foreach (ContentPolicy policy in new[] { Family, Mature })
            {
                IReadOnlyList<OnboardingQuestion> form = ScriptedBotAgent.BuiltInQuestions(policy, mix);
                Assert.True(QuestionValidator.IsValid(form, policy, mix), QuestionValidator.Problem(form, policy, mix));
                Assert.Single(form, q => q.IsEra);
            }
        }
        Assert.All(ScriptedBotAgent.EraCandidates(), q => Assert.True(QuestionValidator.IsValid([q with { Id = "q1" }], Family, 1)));
    }

    [Fact]
    public void The_model_s_era_questions_are_parsed_with_their_decades_and_numbered_with_the_rest()
    {
        const string json = """
            {"questions":[{"kind":"Text","text":"Best snack?","mature":false,"options":[],"maxSelect":0}],
             "eraQuestions":[{"text":"Which console takes you back?","mature":false,"options":[
                {"text":"NES","mature":false,"era":"1980s"},{"text":"PlayStation","mature":false,"era":"1990s"},
                {"text":"Wii","mature":false,"era":"2000s"},{"text":"Switch","mature":false,"era":"2010s"}]}]}
            """;
        IReadOnlyList<OnboardingQuestion> form = FoundryQuestionGenerator.Parse(json);

        Assert.Equal(["q1", "q2"], form.Select(q => q.Id));
        OnboardingQuestion era = Assert.Single(form, q => q.IsEra);
        Assert.Equal(["1980s", "1990s", "2000s", "2010s"], era.Options.Select(o => o.Era));
        Assert.True(QuestionValidator.IsValid(form, Family, new QuestionMix(1, 0, 0, 1)));

        // An older reply without the list still parses.
        Assert.Single(FoundryQuestionGenerator.Parse("""{"questions":[{"kind":"Text","text":"Best snack?","mature":false,"options":[],"maxSelect":0}]}"""));
    }

    [Fact]
    public void The_room_s_eras_are_counted_and_kept_after_the_form()
    {
        OnboardingQuestion era = GoodEra("q1");
        OnboardingQuestion text = new("q2", "Best snack?", false);
        OnboardingAnswer[] answers =
        [
            new("q1", "", ["b"]), new("q1", "", ["b"]), new("q1", "", ["c"]),
            new("q2", "popcorn", [])
        ];

        StyleProfile style = StyleProfile.From([era, text], answers);
        Assert.Equal([new EraCount("1990s", 2), new EraCount("2000s", 1)], style.Eras);
        // Anonymous counts: still there once the answers are taken away.
        Assert.Equal(style.Eras, style.WithoutAnswers().Eras);
        Assert.Empty(style.WithoutAnswers().Choices);
    }

    [Fact]
    public async Task A_bot_knows_when_the_room_grew_up_and_when_it_did_but_no_client_learns_the_decade()
    {
        Harness h = new(humans: 2, o => o.EraQuestions = 1);
        BotContext? seen = null;
        h.Agent.Vote = r => { seen ??= r.Context; return null; };

        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        OnboardingQuestion era = Assert.Single(h.Questions, q => q.IsEra);
        OnboardingAnswer[] answers = h.Answers();
        foreach (Player human in h.Humans)
            Assert.Null(await h.Engine.SubmitOnboardingAsync(human.Id, answers));
        await h.UntilAsync(() => seen is not null, "the first bot vote");

        // Every human picked the first option (see Harness.AnswersFor).
        string expected = era.Options[0].Era!;
        Assert.Equal([new EraCount(expected, 2)], seen!.Style.Eras);
        PersonaAnswer own = Assert.Single(seen.Persona, p => p.Question == era.Text);
        Assert.True(Eras.IsValid(own.Era));

        Assert.All(h.Messages, m => Assert.DoesNotContain("\"era", m.Json, StringComparison.OrdinalIgnoreCase));
    }
}
