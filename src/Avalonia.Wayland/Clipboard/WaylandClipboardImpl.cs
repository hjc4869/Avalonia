using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Wayland.Server;
using Avalonia.Wayland.Server.Transient.Clipboard;
using Avalonia.X11.Selections;

namespace Avalonia.Wayland.Clipboard;

/// <summary>
/// Implements <see cref="IOwnedClipboardImpl"/> for the Wayland backend.
/// All Wayland protocol calls happen on the Wayland thread via <see cref="WaylandWorker"/>.
/// Data I/O (pipe reads/writes) happens on pool threads using .NET async APIs.
/// No shared mutable state between threads — all cross-thread communication via messaging.
/// </summary>
class WaylandClipboardImpl : IOwnedClipboardImpl
{
    private readonly WaylandWorker _worker;

    public WaylandClipboardImpl(WaylandWorker worker)
    {
        _worker = worker;
    }

    public async Task<IAsyncDataTransfer?> TryGetDataAsync()
    {
        var (cookie, mimeTypes) = await _worker.InvokeOobAsync(() =>
        {
            var device = _worker.Globals?.InputDispatcher.GetDataDevice();
            if (device == null)
                return ((WaylandOfferCookie?)null, (string[]?)null);
            return device.GetSelectionInfo();
        }).ConfigureAwait(false);

        if (cookie == null || mimeTypes == null || mimeTypes.Length == 0)
            return null;

        // Build the item layout up front (one item per file + a shared item for the rest), reading
        // the file URI list off the calling thread so item access later doesn't block.
        var transfer = new WaylandDataTransfer(mimeTypes, cookie);
        await transfer.PreloadAsync().ConfigureAwait(false);
        return transfer;
    }

    public Task SetDataAsync(IAsyncDataTransfer dataTransfer)
    {
        var outgoing = new WaylandOutgoingTransfer(dataTransfer);
        return outgoing.SetAsSelection(_worker);
    }

    public Task ClearAsync() => SetDataAsync(new DataTransfer());

    public Task<bool> IsCurrentOwnerAsync()
    {
        return _worker.InvokeOobAsync(() =>
        {
            var device = _worker.Globals?.InputDispatcher.GetDataDevice();
            return device?.ActiveSource != null;
        });
    }
    
    /// <summary>
    /// Serializes data from an <see cref="IAsyncDataTransfer"/> for a specific MIME type directly
    /// into <paramref name="stream"/>. Streaming avoids buffering the whole payload in memory, which
    /// matters for large formats such as bitmaps. Writes nothing if no item provides the format.
    /// </summary>
    internal static async Task SerializeForMimeAsync(IAsyncDataTransfer transfer, string mimeType, Stream stream)
    {
        var format = WaylandMimeMapper.FromMimeType(mimeType);
        if (format == null)
            return;
        
        // Files span multiple items; gather them all and serialize as a text/uri-list.
        if (DataFormat.File.Equals(format))
        {
            if (await transfer.TryGetFilesAsync().ConfigureAwait(false) is { Length: > 0 } files)
                await stream.WriteAsync(UriListHelper.FileUriListToUtf8Bytes(files)).ConfigureAwait(false);
            return;
        }
        
        foreach (var item in transfer.Items)
        {
            var value = await item.TryGetRawAsync(format);
            if (value == null)
                continue;

            if (DataFormat.Text.Equals(format))
            {
                if (value is string text)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
                    return;
                }
            }
            else if (DataFormat.Bitmap.Equals(format))
            {
                if (value is Bitmap bitmap)
                {
                    // Clone the platform bitmap ref so it outlives the UI-thread retrieval, then
                    // encode off the UI thread so the encode never stalls it on pipe backpressure.
                    // The encoder cannot be pointed straight at the pipe: Pipe2Stream is opened in
                    // async mode over a non-blocking fd, which rejects the synchronous writes an
                    // encoder does ("Cannot block a call on this socket while an earlier
                    // asynchronous call is in progress"), and the send handler swallows it, so the
                    // other client would receive nothing at all. Encoding to a buffer first costs
                    // no extra peak memory: Skia encodes into an SKData in full either way.
                    var bitmapRef = bitmap.PlatformImpl.Clone();
                    var encoded = await Task.Run(() =>
                    {
                        using (bitmapRef)
                        {
                            var buffer = new MemoryStream();
                            bitmapRef.Item.Save(buffer, PngBitmapEncoderOptions.Default);
                            return buffer;
                        }
                    }).ConfigureAwait(false);

                    using (encoded)
                    {
                        encoded.Position = 0;
                        await encoded.CopyToAsync(stream).ConfigureAwait(false);
                    }

                    return;
                }
            }
            else if (value is byte[] bytes)
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }
            else if (value is string strValue)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(strValue)).ConfigureAwait(false);
                return;
            }
        }
    }
}
