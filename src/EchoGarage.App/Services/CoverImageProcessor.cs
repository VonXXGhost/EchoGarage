using System.Windows.Media;
using System.Windows.Media.Imaging;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Services;

public sealed record CoverImageInfo(string FormatName, int Width, int Height, long ByteSize, bool IsJpeg);

public sealed record CoverConversionResult(byte[] Bytes, bool Converted);

/// <summary>
/// Decodes embedded cover art, exposes its format/size information and can
/// re-encode it as JPEG with an optional maximum edge length.
/// </summary>
public static class CoverImageProcessor
{
    public const string JpegMime = "image/jpeg";

    public static CoverImageInfo? ReadInfo(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            return new CoverImageInfo(DescribeFormat(decoder), frame.PixelWidth, frame.PixelHeight, bytes.Length, decoder is JpegBitmapDecoder);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-encodes the image as JPEG and applies the max-edge limit. Returns the
    /// original bytes when the conversion policy says the result would not help.
    /// </summary>
    public static CoverConversionResult ConvertToJpeg(byte[] source, int quality, int maxEdge)
    {
        try
        {
            using var stream = new MemoryStream(source);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return new CoverConversionResult(source, false);
            }

            var frame = decoder.Frames[0];
            var sourceIsJpeg = decoder is JpegBitmapDecoder;
            var (targetWidth, targetHeight) = CoverImageSizing.ScaleDown(frame.PixelWidth, frame.PixelHeight, maxEdge);

            BitmapSource bitmap = frame;
            if (targetWidth != frame.PixelWidth || targetHeight != frame.PixelHeight)
            {
                var transformed = new TransformedBitmap(
                    frame,
                    new ScaleTransform((double)targetWidth / frame.PixelWidth, (double)targetHeight / frame.PixelHeight));
                RenderOptions.SetBitmapScalingMode(transformed, BitmapScalingMode.HighQuality);
                bitmap = transformed;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            var converted = output.ToArray();

            var useConverted = CoverConversionPolicy.ShouldConvert(
                sourceIsJpeg,
                frame.PixelWidth,
                frame.PixelHeight,
                maxEdge,
                source.Length,
                converted.Length);

            return useConverted
                ? new CoverConversionResult(converted, true)
                : new CoverConversionResult(source, false);
        }
        catch
        {
            return new CoverConversionResult(source, false);
        }
    }

    private static string DescribeFormat(BitmapDecoder decoder) => decoder switch
    {
        JpegBitmapDecoder => "JPEG",
        PngBitmapDecoder => "PNG",
        BmpBitmapDecoder => "BMP",
        GifBitmapDecoder => "GIF",
        TiffBitmapDecoder => "TIFF",
        _ => decoder.CodecInfo?.FriendlyName ?? "图片"
    };
}
