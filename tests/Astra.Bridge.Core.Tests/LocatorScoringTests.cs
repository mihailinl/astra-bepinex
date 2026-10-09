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
    public void a_camera_that_draws_the_world_beats_a_deeper_ui_camera()
    {
        // Extermination Ship's kind of scene: the player's camera at depth 0, a UI camera above it.
        Assert.True(LocatorScoring.BetterCamera(rank: 2, depth: 0, since: 0, area: 1, bestRank: 1, bestDepth: 5, bestSince: 1000, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(rank: 1, depth: 5, since: 1000, area: 1000, bestRank: 2, bestDepth: 0, bestSince: 0, bestArea: 1));
    }

    [Fact]
    public void a_camera_ranks_by_what_it_draws()
    {
        Assert.Equal(2, LocatorScoring.CameraRank(rendering: true, overlay: false, orthographic: false, cullingMask: -1));
        Assert.Equal(2, LocatorScoring.CameraRank(rendering: true, overlay: false, orthographic: false, cullingMask: 1 << 9)); // a game's own layers only
        Assert.Equal(1, LocatorScoring.CameraRank(rendering: true, overlay: false, orthographic: true, cullingMask: -1));
        Assert.Equal(1, LocatorScoring.CameraRank(rendering: true, overlay: false, orthographic: false, cullingMask: LocatorScoring.UiLayerBit));
        Assert.Equal(0, LocatorScoring.CameraRank(rendering: true, overlay: true, orthographic: false, cullingMask: -1));
        Assert.Equal(0, LocatorScoring.CameraRank(rendering: false, overlay: false, orthographic: false, cullingMask: -1));
    }

    // Masks read from the games' own logs: ULTRAKILL's main camera and its HUD camera, MiSide's main
    // camera and its 'CameraPersons'.
    const int UltrakillMain = unchecked((int)0x8fd2dfd7), UltrakillHud = 0x2000;
    const int MiSideMain = 0x13a45, MiSidePersons = 0x1a0;

    [Fact]
    public void a_urp_overlay_is_an_overlay()
    {
        // URP's own camera type answers, whatever the camera's pose, clearing or layers.
        Assert.True(LocatorScoring.IsOverlay(knownOverlay: true, sharesViewWithLowerDepthCamera: false, keepsPictureBeneath: false,
            cullingMask: -1, beneathMask: 1));
    }

    [Fact]
    public void a_built_in_hud_camera_that_clears_depth_only_is_an_overlay()
    {
        // ULTRAKILL's 'HUD Camera': the main camera's view, a higher depth, Clear Flags = Depth only,
        // one layer the main camera does not draw.
        Assert.True(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: true, keepsPictureBeneath: true,
            cullingMask: UltrakillHud, beneathMask: UltrakillMain));
        Assert.True(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: true, keepsPictureBeneath: true,
            cullingMask: MiSidePersons, beneathMask: MiSideMain));
    }

    [Fact]
    public void the_near_half_of_a_near_far_split_is_the_worlds_camera_not_an_overlay()
    {
        // Built-in: a 'far' camera at depth -2 (Skybox, 50-5000 m) and the tagged main camera at -1
        // (depth only, 0.1-50 m), a child at the same pose, both drawing the same layers. The main
        // camera keeps the far picture, but it is the world's camera: never capped below the far one.
        Assert.False(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: true, keepsPictureBeneath: true,
            cullingMask: -1, beneathMask: -1));
        Assert.False(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: true, keepsPictureBeneath: true,
            cullingMask: UltrakillMain, beneathMask: UltrakillMain));
        // A near half that draws MORE than the far one (the far one skips the small props) neither.
        Assert.False(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: true, keepsPictureBeneath: true,
            cullingMask: -1, beneathMask: 0x0f));
    }

    [Fact]
    public void a_second_camera_that_paints_its_own_picture_is_not_an_overlay()
    {
        // The same view, few layers, but it does not keep the picture beneath: a Built-in camera that
        // clears its colour, or a URP Base camera (a leftover or a duplicate), whose type is known.
        Assert.False(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: true, keepsPictureBeneath: false,
            cullingMask: UltrakillHud, beneathMask: UltrakillMain));
    }

    [Fact]
    public void a_camera_at_another_pose_is_never_an_overlay()
    {
        foreach (bool keeps in new[] { false, true })
            Assert.False(LocatorScoring.IsOverlay(knownOverlay: false, sharesViewWithLowerDepthCamera: false, keepsPictureBeneath: keeps,
                cullingMask: UltrakillHud, beneathMask: UltrakillMain));
    }

    [Fact]
    public void layers_are_counted_over_all_32_bits()
    {
        Assert.Equal(0, LocatorScoring.Layers(0));
        Assert.Equal(32, LocatorScoring.Layers(-1));
        Assert.Equal(1, LocatorScoring.Layers(int.MinValue));
        Assert.Equal(3, LocatorScoring.Layers(MiSidePersons));
        Assert.True(LocatorScoring.DrawsFewerLayers(UltrakillHud, UltrakillMain));
        Assert.False(LocatorScoring.DrawsFewerLayers(UltrakillMain, UltrakillHud));
        Assert.False(LocatorScoring.DrawsFewerLayers(-1, -1));
    }

    [Fact]
    public void a_camera_deeper_wins_regardless_of_the_rest()
    {
        Assert.True(LocatorScoring.BetterCamera(rank: 2, depth: 1, since: 0, area: 1, bestRank: 2, bestDepth: 0, bestSince: 1000, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(rank: 2, depth: 0, since: 1000, area: 1000, bestRank: 2, bestDepth: 1, bestSince: 0, bestArea: 0));
    }

    [Fact]
    public void a_depth_tie_goes_to_whichever_moved_more_recently()
    {
        Assert.True(LocatorScoring.BetterCamera(rank: 2, depth: 5, since: 10, area: 1, bestRank: 2, bestDepth: 5, bestSince: 2, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(rank: 2, depth: 5, since: 2, area: 1000, bestRank: 2, bestDepth: 5, bestSince: 10, bestArea: 1));
    }

    [Fact]
    public void a_depth_and_movement_tie_goes_to_the_larger_viewport()
    {
        Assert.True(LocatorScoring.BetterCamera(rank: 2, depth: 5, since: 10, area: 2000, bestRank: 2, bestDepth: 5, bestSince: 10, bestArea: 1000));
        Assert.False(LocatorScoring.BetterCamera(rank: 2, depth: 5, since: 10, area: 500, bestRank: 2, bestDepth: 5, bestSince: 10, bestArea: 1000));
    }

    [Fact]
    public void a_full_tie_is_not_better()
    {
        Assert.False(LocatorScoring.BetterCamera(rank: 2, depth: 5, since: 10, area: 100, bestRank: 2, bestDepth: 5, bestSince: 10, bestArea: 100));
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
