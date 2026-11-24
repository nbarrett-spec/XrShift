using System.Numerics;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class StereoTests
{
    static readonly DisplayOptics Optics = DisplayOptics.OnePro;
    const float Half = Camera.DefaultEyeSeparation / 2f;

    /// <summary>Where a point straight ahead lands for one eye, in pixels from the middle of that eye's picture (+ = right).</summary>
    static float OffCentre(float distance, float eyeX)
    {
        Vector4 clip = Vector4.Transform(new Vector4(0, 0, -distance, 1), Camera.EyeView(Quaternion.Identity, eyeX) * Optics.Projection());
        return clip.X / clip.W * Optics.Width / 2f;
    }

    [Fact]
    public void SomethingStraightAheadSitsInwardForEachEyeByTheSameAmount()
    {
        float left = OffCentre(2f, -Half), right = OffCentre(2f, Half);
        Assert.True(left > 0 && right < 0); // the left eye sees it to the right of its middle, the right eye to the left
        Assert.Equal(left, -right, 3);
    }

    [Fact]
    public void NearThingsAreFurtherApartBetweenTheEyesThanFarThings()
    {
        float bot = OffCentre(1.5f, -Half) - OffCentre(1.5f, Half);
        float screen = OffCentre(2f, -Half) - OffCentre(2f, Half);
        float horizon = OffCentre(150f, -Half) - OffCentre(150f, Half);
        Assert.True(bot > screen && screen > horizon);
        Assert.True(horizon < 1f); // far away: the same place for both eyes, to within a pixel
        // A screen 2 m away: the eyes' lines of sight differ by atan(0.063 / 2) = 1.8 degrees.
        Assert.Equal(1.8f * Optics.PixelsPerDegree, screen, 0);
    }

    [Fact]
    public void WithNoSeparationAnEyeSeesWhatTheSingleViewDoes() =>
        Assert.Equal(Camera.View(Quaternion.Identity), Camera.EyeView(Quaternion.Identity, 0));

    [Fact]
    public void AFrameTwiceAsWideCarriesTwoFullPictures()
    {
        Assert.Equal((0f, 0f, 1920f, 1080f), Camera.EyeViewport(3840, 1080, Optics, rightEye: false));
        Assert.Equal((1920f, 0f, 1920f, 1080f), Camera.EyeViewport(3840, 1080, Optics, rightEye: true));
    }

    [Fact]
    public void ANormalFrameCarriesTwoPicturesSqueezedToHalfWidth()
    {
        Assert.Equal((0f, 0f, 960f, 1080f), Camera.EyeViewport(1920, 1080, Optics, rightEye: false));
        Assert.Equal((960f, 0f, 960f, 1080f), Camera.EyeViewport(1920, 1080, Optics, rightEye: true));
    }
}
