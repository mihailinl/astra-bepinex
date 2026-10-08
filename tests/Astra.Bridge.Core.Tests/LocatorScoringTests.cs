// SPDX-License-Identifier: MIT
using Astra.Bridge;
using Xunit;

/// <summary>
/// The pure tie-breaks behind the Unity foundation's "find the camera / the player in ANY Unity
/// game" defaults (<c>Astra.Unity.DefaultCamera</c> / <c>DefaultPlayer</c>), away from UnityEngine.
/// </summary>
public class LocatorScoringTests
{
    [Fact]
    public void a_camera_deeper_wins_regardless_of_the_rest()
    {
        Assert.True(LocatorScoring.BetterCamera(depth: 1, since: 0, area: 1, bestDepth: 0, bestSince: 1000, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(depth: 0, since: 1000, area: 1000, bestDepth: 1, bestSince: 0, bestArea: 0));
    }

    [Fact]
    public void a_depth_tie_goes_to_whichever_moved_more_recently()
    {
        Assert.True(LocatorScoring.BetterCamera(depth: 5, since: 10, area: 1, bestDepth: 5, bestSince: 2, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(depth: 5, since: 2, area: 1000, bestDepth: 5, bestSince: 10, bestArea: 1));
    }

    [Fact]
    public void a_depth_and_movement_tie_goes_to_the_larger_viewport()
    {
        Assert.True(LocatorScoring.BetterCamera(depth: 5, since: 10, area: 2000, bestDepth: 5, bestSince: 10, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(depth: 5, since: 10, area: 500, bestDepth: 5, bestSince: 10, bestArea: 1000));
    }

    [Fact]
    public void a_full_tie_is_not_better()
    {
        Assert.False(LocatorScoring.BetterCamera(depth: 5, since: 10, area: 100, bestDepth: 5, bestSince: 10, bestArea: 100));
    }

    [Fact]
    public void any_distance_ahead_of_the_camera_beats_any_distance_within_range_behind_it()
    {
        // 14.9 m ahead vs 0.1 m behind, inside a 15 m range: ahead still wins.
        double ahead = LocatorScoring.CandidateScore(14.9, aheadOfCamera: true, maxDist: 15);
        double behind = LocatorScoring.CandidateScore(0.1, aheadOfCamera: false, maxDist: 15);
        Assert.True(ahead < behind);
    }

    [Fact]
    public void within_the_same_side_the_nearest_wins()
    {
        Assert.True(LocatorScoring.CandidateScore(2, true, 15) < LocatorScoring.CandidateScore(5, true, 15));
        Assert.True(LocatorScoring.CandidateScore(2, false, 15) < LocatorScoring.CandidateScore(5, false, 15));
    }
}
