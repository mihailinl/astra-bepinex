// SPDX-License-Identifier: MIT
using Astra.Bridge;
using Xunit;

/// <summary>
/// <see cref="AnchorHeight"/>: where on her body the point a placement names sits, so the foundation finds
/// the middle of her body from it (her picture is looked up through the plane there).
/// </summary>
public class AnchorHeightTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("feet")]
    public void No_anchor_and_feet_are_her_feet(string anchor) => Assert.Equal(0, AnchorHeight.Of(anchor));

    [Fact]
    public void Her_hips_are_about_her_middle_and_her_head_near_the_top()
    {
        Assert.InRange(AnchorHeight.Of("hips"), 0.45, 0.6);
        Assert.InRange(AnchorHeight.Of("head"), 0.85, 0.95);
        Assert.True(AnchorHeight.Of("spine") > AnchorHeight.Of("hips"));
        Assert.True(AnchorHeight.Of("chest") < AnchorHeight.Of("upperChest"));
    }

    [Theory]
    [InlineData("leftFoot", "rightFoot")]
    [InlineData("leftUpperLeg", "rightUpperLeg")]
    [InlineData("Hips", "hips")]
    [InlineData(" HEAD ", "head")]
    public void Sides_and_case_do_not_matter(string a, string b) => Assert.Equal(AnchorHeight.Of(a), AnchorHeight.Of(b));

    [Theory]
    [InlineData("leftHand")]
    [InlineData("tail")]
    [InlineData("left")]
    public void A_bone_it_does_not_place_is_her_middle(string anchor) => Assert.Equal(0.5, AnchorHeight.Of(anchor));
}
