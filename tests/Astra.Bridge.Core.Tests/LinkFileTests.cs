// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using Astra.Bridge;
using Xunit;

/// <summary><see cref="LinkFile"/>: Astra's link file (link-v1.md §2), found and checked before any socket.</summary>
public class LinkFileTests
{
    const string Id = "knlce.valheim";
    const long Now = 1_800_000_000_000;

    static string File_(string url = "ws://127.0.0.1:41234/link", string token = "3f1a9c2e7b6d4805a1c9e2f6b7d84e1a", string integration = Id,
        string game = "892970", object expires = null, int schema = 1) =>
        "{\"schema\":" + schema + ",\"url\":\"" + url + "\",\"token\":\"" + token + "\",\"integration\":\"" + integration + "\"," +
        "\"game\":\"" + game + "\",\"issued_unix_ms\":" + (Now - 1000) + ",\"expires_unix_ms\":" + (expires ?? (Now + 600_000)) + "}";

    [Fact]
    public void A_good_file_says_where_the_socket_is_and_holds_the_token()
    {
        var f = LinkFile.Parse(File_(), "p", Id, Now, out var why);
        Assert.Null(why);
        Assert.Equal(("127.0.0.1", 41234, "/link"), (f.Host, f.Port, f.UrlPath));
        Assert.Equal("3f1a9c2e7b6d4805a1c9e2f6b7d84e1a", f.Token);
        Assert.Equal("892970", f.Game);
        Assert.Equal(Now + 600_000, f.ExpiresUnixMs);
    }

    [Theory]
    [InlineData("ws://localhost:41234/link")]
    [InlineData("ws://10.0.0.5:41234/link")]
    [InlineData("wss://127.0.0.1:41234/link")]
    [InlineData("ws://127.0.0.1:0/link")]
    [InlineData("ws://127.0.0.1:99999/link")]
    [InlineData("ws://127.0.0.1:41234/link HTTP/1.1")]
    [InlineData("ws://127.0.0.1:41234/link?x=1")]
    public void A_url_anywhere_but_loopback_or_with_a_path_it_would_not_send_is_refused(string url)
    {
        Assert.Null(LinkFile.Parse(File_(url: url), "p", Id, Now, out var why));
        Assert.Contains("url", why);
    }

    [Fact]
    public void A_file_held_open_for_writing_is_read_and_reading_it_never_holds_it_in_place()
    {
        string dir = Path.Combine(Path.GetTempPath(), "astra-linkfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string path = Path.Combine(dir, LinkFile.FileName);
            File.WriteAllText(path, File_(expires: now + 600_000));
            // Someone holds it open for writing (the launcher mid-write): it is still read.
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                Assert.NotNull(LinkFile.Read(path, Id, now, out _));
            // Opened the way LinkFile.Read opens it, the file can still be rewritten and removed (a new
            // Play replacing it), which a reader sharing only Read would refuse on Windows.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using (var w = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    w.SetLength(0);
                File.Delete(path);
            }
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Another_schema_another_integration_or_an_expired_file_is_refused()
    {
        Assert.Null(LinkFile.Parse(File_(schema: 2), "p", Id, Now, out var why));
        Assert.Contains("schema", why);
        Assert.Null(LinkFile.Parse(File_(integration: "someone.else"), "p", Id, Now, out why));
        Assert.Contains("someone.else", why);
        Assert.Null(LinkFile.Parse(File_(expires: Now), "p", Id, Now, out why));
        Assert.Contains("expired", why);
        Assert.Null(LinkFile.Parse(File_(token: ""), "p", Id, Now, out why));
        Assert.Contains("token", why);
        Assert.Null(LinkFile.Parse(File_(token: "has space"), "p", Id, Now, out why));
        Assert.Null(LinkFile.Parse("{\"schema\":1}", "p", Id, Now, out why));
        Assert.Null(LinkFile.Parse("not json", "p", Id, Now, out why));
        Assert.Contains("not JSON", why);
    }

    [Fact]
    public void The_variable_wins_over_the_profile_and_no_file_is_no_link()
    {
        string dir = Path.Combine(Path.GetTempPath(), "astra-linkfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var none = new Dictionary<string, string>();
            Assert.Null(LinkFile.Locate(dir, k => none.TryGetValue(k, out var v) ? v : null));
            string inProfile = Path.Combine(dir, LinkFile.FileName);
            File.WriteAllText(inProfile, File_());
            Assert.Equal(inProfile, LinkFile.Locate(dir, _ => null));
            string named = Path.Combine(dir, "elsewhere.json");
            File.WriteAllText(named, File_());
            Assert.Equal(named, LinkFile.Locate(dir, k => k == LinkFile.EnvVar ? named : null));
            // A variable naming a file that is not there: the profile's.
            Assert.Equal(inProfile, LinkFile.Locate(dir, k => k == LinkFile.EnvVar ? named + ".gone" : null));
            // A file larger than any link file is not read.
            File.WriteAllText(inProfile, File_() + new string(' ', LinkFile.MaxBytes));
            Assert.Null(LinkFile.Read(inProfile, Id, Now, out var why));
            Assert.Contains("bytes", why);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_profiles_link_folder_is_where_a_game_steam_started_finds_the_file()
    {
        // Launcher 0.6.1 and later write <profile>/link/astra-link.json, and a game Steam starts has
        // no ASTRA_LINK_FILE (the Link stand, 2026-11-19: Valheim found no file and ran without Link).
        string dir = Path.Combine(Path.GetTempPath(), "astra-linkfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, LinkFile.Folder));
        try
        {
            string inFolder = Path.Combine(dir, LinkFile.Folder, LinkFile.FileName);
            File.WriteAllText(inFolder, File_());
            Assert.Equal(inFolder, LinkFile.Locate(dir, _ => null));
            // A file 0.6.0 left at the root loses to the folder's…
            string atRoot = Path.Combine(dir, LinkFile.FileName);
            File.WriteAllText(atRoot, File_());
            Assert.Equal(inFolder, LinkFile.Locate(dir, _ => null));
            // …and is still read where the folder holds none.
            File.Delete(inFolder);
            Assert.Equal(atRoot, LinkFile.Locate(dir, _ => null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Json_values_are_read_whole_and_nesting_is_bounded()
    {
        var v = (Dictionary<string, object>)JsonReader.ReadValue("{\"a\":{\"b\":[1,\"x\",true,null,{\"c\":2}]},\"a2\":[]}");
        var list = (List<object>)((Dictionary<string, object>)v["a"])["b"];
        Assert.Equal(new object[] { 1.0, "x", true, null }, list.GetRange(0, 4));
        Assert.Equal(2.0, ((Dictionary<string, object>)list[4])["c"]);
        Assert.Empty((List<object>)v["a2"]);
        Assert.Throws<FormatException>(() => JsonReader.ReadValue(new string('[', JsonReader.MaxDepth + 1) + new string(']', JsonReader.MaxDepth + 1)));
        Assert.NotNull(JsonReader.ReadValue(new string('[', JsonReader.MaxDepth) + new string(']', JsonReader.MaxDepth)));
        Assert.Throws<FormatException>(() => JsonReader.ReadValue("{\"a\":1} x"));
        // One answer on every runtime: Mono throws OverflowException on 1e400, CoreCLR gives infinity.
        Assert.Throws<FormatException>(() => JsonReader.ReadValue("{\"a\":1e400}"));
        Assert.Throws<FormatException>(() => JsonReader.ReadObject("{\"a\":-1e400}"));
        // ReadObject skips what it does not keep, and that skip is bounded too.
        string deep = new string('[', 3 * JsonReader.MaxDepth) + new string(']', 3 * JsonReader.MaxDepth);
        Assert.Throws<FormatException>(() => JsonReader.ReadObject("{\"a\":" + deep + "}"));
        Assert.Equal(1.0, JsonReader.ReadObject("{\"a\":[[[1]]],\"b\":1}")["b"]);
    }
}
