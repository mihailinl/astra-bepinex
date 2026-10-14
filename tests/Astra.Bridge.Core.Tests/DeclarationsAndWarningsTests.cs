// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Bridge;
using Xunit;

/// <summary>
/// <c>hello.params</c> (the raw parameters a game declares) and the engine's <c>warn</c> messages, as
/// PROTOCOL.md specifies them: what the foundation writes, and what it prints.
/// </summary>
public class DeclarationsAndWarningsTests
{
    static readonly string[] Warn = { "warn" };

    [Fact]
    public void the_hello_lists_its_caps_and_its_parameters_in_order()
    {
        var declared = new[] { ParamDecl.Speed, ParamDecl.Airborne, new ParamDecl("climbing", ParamType.Bool) };
        Assert.Equal(
            "{\"t\":\"hello\",\"v\":1,\"client\":\"g\",\"integration\":\"astra.g\",\"caps\":[\"warn\"],"
            + "\"params\":[{\"name\":\"speed\",\"type\":\"float\"},{\"name\":\"airborne\",\"type\":\"bool\"},{\"name\":\"climbing\",\"type\":\"bool\"}]}",
            Messages.Hello("g", null, null, null, null, "astra.g", false, declared, Warn));
    }

    /// <summary>Null is "not said" (no field); an empty list is "I send none" (an empty field).</summary>
    [Fact]
    public void no_list_says_nothing_and_an_empty_list_says_none()
    {
        Assert.Equal("{\"t\":\"hello\",\"v\":1,\"client\":\"g\"}", Messages.Hello("g", null, null, null, null, null, false, null, null));
        Assert.Equal("{\"t\":\"hello\",\"v\":1,\"client\":\"g\",\"params\":[]}",
            Messages.Hello("g", null, null, null, null, null, false, new ParamDecl[0], new string[0]));
    }

    /// <summary>What the engine would set aside never leaves: a bad name, a repeated one, any past the 64th.</summary>
    [Fact]
    public void the_hello_leaves_out_what_the_engine_would_refuse()
    {
        var list = new List<ParamDecl>
        {
            new ParamDecl("ok", ParamType.Bool), new ParamDecl("1x", ParamType.Bool), new ParamDecl("a b", ParamType.Float),
            new ParamDecl(null, ParamType.Float), new ParamDecl("ok", ParamType.Float), new ParamDecl(new string('x', 33), ParamType.Float),
        };
        Assert.Equal("{\"t\":\"hello\",\"v\":1,\"client\":\"g\",\"params\":[{\"name\":\"ok\",\"type\":\"bool\"}]}",
            Messages.Hello("g", null, null, null, null, null, false, list, null));

        var many = Enumerable.Range(0, 70).Select(i => new ParamDecl("p" + i, ParamType.Float)).ToList();
        var hello = Messages.Hello("g", null, null, null, null, null, false, many, null);
        Assert.Equal(ParamDeclarations.MaxCount, hello.Split("\"name\"").Length - 1);
        Assert.Contains("\"p63\"", hello);
        Assert.DoesNotContain("\"p64\"", hello);
    }

    [Theory]
    [InlineData("climbing", true)]
    [InlineData("_x9", true)]
    [InlineData("in_ship", true)]
    [InlineData("abcdefghijabcdefghijabcdefghij12", true)] // 32
    [InlineData("abcdefghijabcdefghijabcdefghij123", false)] // 33
    [InlineData("9lives", false)]
    [InlineData("a-b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("café", false)]
    public void a_parameter_name_is_an_ascii_identifier_of_at_most_32(string name, bool valid) =>
        Assert.Equal(valid, ParamDecl.ValidName(name));

    [Fact]
    public void declarations_keep_the_measured_ones_first_and_each_name_once()
    {
        var d = new ParamDeclarations(ParamDecl.Speed, ParamDecl.Airborne);
        Assert.Equal(DeclareOutcome.Added, d.Declare("climbing", ParamType.Bool));
        Assert.Equal(DeclareOutcome.Added, d.Declare("depth", ParamType.Float));
        Assert.Equal(DeclareOutcome.AlreadyDeclared, d.Declare("climbing", ParamType.Bool)); // the same: nothing
        Assert.Equal(new[] { "speed", "airborne", "climbing", "depth" }, d.ToArray().Select(p => p.Name));
        Assert.Equal(new[] { ParamType.Float, ParamType.Bool, ParamType.Bool, ParamType.Float }, d.ToArray().Select(p => p.Type));
    }

    [Fact]
    public void a_wrong_declaration_throws_and_changes_nothing()
    {
        var d = new ParamDeclarations(ParamDecl.Speed, ParamDecl.Airborne);
        d.Declare("climbing", ParamType.Bool);
        Assert.Throws<ArgumentException>(() => d.Declare("not a name", ParamType.Bool));
        Assert.Throws<ArgumentException>(() => d.Declare("speed", ParamType.Float)); // the foundation's own
        Assert.Throws<ArgumentException>(() => d.Declare("climbing", ParamType.Float)); // the other type
        Assert.Throws<ArgumentException>(() => d.Declare("x", (ParamType)7));
        Assert.Equal(3, d.Count);

        for (int i = d.Count; i < ParamDeclarations.MaxCount; i++) d.Declare("p" + i, ParamType.Float);
        Assert.Throws<ArgumentException>(() => d.Declare("one_too_many", ParamType.Float));
        Assert.Equal(ParamDeclarations.MaxCount, d.Count);
    }

    /// <summary>F6: 64 in all is the engine's bound, so 62 beside the foundation's two, and the
    /// refusal says so.</summary>
    [Fact]
    public void an_integration_declares_62_beside_the_measured_two()
    {
        var d = new ParamDeclarations(ParamDecl.Speed, ParamDecl.Airborne);
        for (int i = 0; i < 62; i++) d.Declare("p" + i, ParamType.Bool);
        var e = Assert.Throws<ArgumentException>(() => d.Declare("p62", ParamType.Bool));
        Assert.Contains("62 beside the foundation's 2", e.Message);
    }

    [Fact]
    public void declarations_are_said_once_a_hello_took_them()
    {
        var d = new ParamDeclarations(ParamDecl.Speed);
        d.Declare("a", ParamType.Bool);
        Assert.False(d.Said);
        var forHello = d.ForHello();
        Assert.True(d.Said);
        Assert.Equal(new[] { "speed", "a" }, forHello.Select(p => p.Name));
        Assert.Equal(DeclareOutcome.AddedAfterHello, d.Declare("b", ParamType.Bool));
        Assert.Equal(DeclareOutcome.AlreadyDeclared, d.Declare("a", ParamType.Bool));
    }

    /// <summary>A declaration racing a hello on another thread is in that hello's list or reported
    /// as late — never neither (the game's thread declares while the link's thread says hello).</summary>
    [Fact]
    public void a_declaration_racing_a_hello_is_said_or_reported_late()
    {
        const int Trials = 20000;
        var lists = new ParamDeclarations[Trials];
        for (int i = 0; i < Trials; i++) lists[i] = new ParamDeclarations(ParamDecl.Speed);
        var outcomes = new DeclareOutcome[Trials];
        var hellos = new ParamDecl[Trials][];
        int turn = -1, ready = 0;
        var link = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < Trials; i++)
            {
                while (System.Threading.Volatile.Read(ref turn) < i) { }
                System.Threading.Interlocked.Increment(ref ready);
                hellos[i] = lists[i].ForHello();
            }
        });
        link.Start();
        for (int i = 0; i < Trials; i++)
        {
            System.Threading.Volatile.Write(ref turn, i);
            while (System.Threading.Volatile.Read(ref ready) <= i) { }
            outcomes[i] = lists[i].Declare("x", ParamType.Bool);
        }
        link.Join();
        for (int i = 0; i < Trials; i++)
        {
            bool inHello = hellos[i].Any(p => p.Name == "x");
            Assert.True(inHello || outcomes[i] == DeclareOutcome.AddedAfterHello, $"trial {i}: neither in the hello nor reported late");
            Assert.False(inHello && outcomes[i] == DeclareOutcome.AddedAfterHello, $"trial {i}: in the hello AND reported late");
        }
    }

    /// <summary>A copy: what the link thread holds never changes under it.</summary>
    [Fact]
    public void declarations_hand_out_a_copy()
    {
        var d = new ParamDeclarations();
        d.Declare("a", ParamType.Bool);
        var handed = d.ToArray();
        d.Declare("b", ParamType.Bool);
        Assert.Single(handed);
        Assert.Equal(2, d.ToArray().Length);
    }

    [Fact]
    public void each_warning_is_said_once_in_plain_words()
    {
        var log = new WarnLog();
        var first = log.Admit("param_undeclared", "climbing", null);
        Assert.Contains("does not declare the parameter 'climbing'", first);
        Assert.Null(log.Admit("param_undeclared", "climbing", null));
        Assert.Contains("'depth'", log.Admit("param_undeclared", "depth", null));
        Assert.Contains("no state 'seat'", log.Admit("state_unknown", "seat", null));
        Assert.Contains("'wave' is for an animation set she is not wearing", log.Admit("cue_wrong_pack", "wave", null));
        Assert.Equal("Astra warns: the clip 'c.vrma' was refused: capture is off",
            log.Admit("clip_refused", "c.vrma", "capture is off"));
        Assert.Equal("Astra warns (new_code): 'x'", log.Admit("new_code", "x", ""));
        Assert.Equal("Astra warns (unknown): 'y'", log.Admit(null, "y", null));
    }

    /// <summary>F7: what the foundation measures in every game is no warning anybody can act on.</summary>
    [Fact]
    public void the_measured_parameters_are_never_warned_about()
    {
        var log = new WarnLog(ParamDecl.Speed.Name, ParamDecl.Airborne.Name);
        Assert.Null(log.Admit("param_undeclared", "speed", null));
        Assert.Null(log.Admit("param_undeclared", "airborne", null));
        Assert.NotNull(log.Admit("param_undeclared", "climbing", null));
        Assert.NotNull(log.Admit("state_unknown", "speed", null)); // only the parameter warning is quiet
    }

    [Fact]
    public void what_the_engine_echoes_is_cut_and_made_printable()
    {
        var log = new WarnLog();
        var line = log.Admit("param_undeclared", "a\nb\u202Ec" + new string('z', 100), null);
        Assert.False(line.IndexOf('\n') >= 0);
        Assert.False(line.IndexOf('\u202E') >= 0); // ordinal: a culture comparison ignores it
        Assert.Contains("'abc" + new string('z', 61) + "'", line); // 64 characters kept
        var why = log.Admit("clip_refused", "f", new string('w', 300));
        Assert.EndsWith(": " + new string('w', 200), why);
    }

    [Fact]
    public void past_the_budget_one_line_says_the_rest_are_not_shown()
    {
        var log = new WarnLog();
        for (int i = 0; i < WarnLog.Max; i++) Assert.NotNull(log.Admit("param_undeclared", "p" + i, null));
        Assert.Contains("the rest are not shown", log.Admit("param_undeclared", "more", null));
        Assert.Null(log.Admit("param_undeclared", "even_more", null));
        Assert.Null(log.Admit("param_undeclared", "p0", null));
    }
}
