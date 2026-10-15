// SPDX-License-Identifier: MIT
using Astra.Bridge;
using Xunit;

/// <summary>
/// The reading at the end of the foundation's "her:" line (<c>Astra.Unity.WhereLine</c>), which
/// CONTRIBUTING.md tells players to report: each case must name the part that is at fault.
/// </summary>
public class HerDiagnosisTests
{
    [Fact]
    public void empty_pictures_are_not_blamed_on_the_drawing()
    {
        // She stood behind the player: every picture was an empty crop, so nothing was drawn — by design.
        Assert.Equal(" (Astra's picture is empty)", HerDiagnosis.Why(shown: true, pictures: 503, drawn: 0, empty: 1006, cover: 0));
        // Even when the coverage sample is older than the period (it is measured every 5 s).
        Assert.Equal(" (Astra's picture is empty)", HerDiagnosis.Why(shown: true, pictures: 503, drawn: 0, empty: 1006, cover: 0.04f));
        Assert.True(HerDiagnosis.IsEmpty(drawn: 0, empty: 1006, cover: 0.04f));
    }

    [Fact]
    public void pictures_with_her_that_are_never_drawn_are_the_drawings_fault()
    {
        Assert.Equal(" (pictures arrive, none is drawn)", HerDiagnosis.Why(shown: true, pictures: 600, drawn: 0, empty: 0, cover: 0.2f));
        Assert.Equal(" (pictures arrive, none is drawn)", HerDiagnosis.Why(shown: true, pictures: 600, drawn: 0, empty: 0, cover: -1));
        Assert.False(HerDiagnosis.IsEmpty(drawn: 0, empty: 0, cover: 0.2f));
    }

    [Fact]
    public void the_other_readings_keep_their_order()
    {
        Assert.Equal(" (hidden: toggled off, or Astra not connected)", HerDiagnosis.Why(false, 0, 0, 0, -1));
        Assert.Equal(" (no picture from Astra)", HerDiagnosis.Why(true, 0, 0, 0, -1));
        Assert.Equal(" (Astra's picture is empty)", HerDiagnosis.Why(true, 600, 600, 0, 0));
        Assert.Equal("", HerDiagnosis.Why(true, 600, 590, 10, 0.06f));
    }
}
