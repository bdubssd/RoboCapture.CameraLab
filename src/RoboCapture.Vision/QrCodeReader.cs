using OpenCvSharp;
using ZXing;

namespace RoboCapture.Vision;

/// <summary>
/// Decodes a QR code from a live-view (or any) JPEG frame — the camera-based alternative to a
/// physical wedge scanner for subject identification. Uses ZXing.Net (Apache-2.0, fully
/// offline). A pure function over image bytes, same shape as <see cref="IShotQualityFilter"/>:
/// no camera dependency, testable against a fixture image.
/// </summary>
public sealed class QrCodeReader
{
    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new ZXing.Common.DecodingOptions
        {
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryHarder = true
        }
    };

    /// <summary>Returns the decoded text, or null if no QR code was found in the frame.</summary>
    public string? Decode(byte[] jpegBytes)
    {
        using var decoded = Cv2.ImDecode(jpegBytes, ImreadModes.Grayscale);
        if (decoded.Empty()) return null;

        // RGBLuminanceSource expects a tightly packed width*height buffer with no row padding;
        // Mat rows can be padded, so force a contiguous copy rather than assuming Step() == Cols.
        using var image = decoded.IsContinuous() ? decoded : decoded.Clone();
        var buffer = new byte[image.Rows * image.Cols];
        System.Runtime.InteropServices.Marshal.Copy(image.Data, buffer, 0, buffer.Length);
        var luminanceSource = new RGBLuminanceSource(buffer, image.Cols, image.Rows, RGBLuminanceSource.BitmapFormat.Gray8);

        var result = _reader.Decode(luminanceSource);
        return result?.Text;
    }
}
