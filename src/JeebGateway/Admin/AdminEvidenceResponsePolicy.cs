using Microsoft.Net.Http.Headers;

namespace JeebGateway.Admin;

internal static class AdminEvidenceResponsePolicy
{
    // Evidence is streamed only when the owner supplies a bounded length. This
    // avoids turning an admin browser request into an unbounded CDN relay.
    internal const long MaxContentLengthBytes = 25L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> Allowed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = "jpg",
            ["image/png"] = "png",
            ["image/webp"] = "webp",
            ["image/gif"] = "gif",
            ["image/avif"] = "avif",
            ["application/pdf"] = "pdf",
        };

    public static bool TryApply(HttpResponse response, string? rawContentType, out string contentType)
    {
        contentType = rawContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        response.Headers.CacheControl = "private, no-store";
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (!Allowed.TryGetValue(contentType, out var extension)) return false;
        response.Headers[HeaderNames.ContentDisposition] =
            $"attachment; filename=\"evidence.{extension}\"";
        return true;
    }

    public static bool HasSafeLength(long? contentLength) =>
        contentLength is > 0 and <= MaxContentLengthBytes;

    public static Stream EnforceDeclaredLength(Stream source, long declaredLength) =>
        new ExactLengthReadStream(source, declaredLength);

    private sealed class ExactLengthReadStream(Stream source, long declaredLength) : Stream
    {
        private long _remaining = declaredLength;
        private bool _endVerified;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => declaredLength;
        public override long Position { get => declaredLength - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining == 0) return VerifyEnd();
            var read = source.Read(buffer, offset, (int)Math.Min(count, _remaining));
            if (read == 0) throw new InvalidDataException("Evidence ended before its declared Content-Length.");
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return await VerifyEndAsync(cancellationToken);
            var read = await source.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            if (read == 0) throw new InvalidDataException("Evidence ended before its declared Content-Length.");
            _remaining -= read;
            return read;
        }

        private int VerifyEnd()
        {
            if (_endVerified) return 0;
            if (source.ReadByte() != -1)
                throw new InvalidDataException("Evidence exceeded its declared Content-Length.");
            _endVerified = true;
            return 0;
        }

        private async ValueTask<int> VerifyEndAsync(CancellationToken cancellationToken)
        {
            if (_endVerified) return 0;
            var probe = new byte[1];
            if (await source.ReadAsync(probe, cancellationToken) != 0)
                throw new InvalidDataException("Evidence exceeded its declared Content-Length.");
            _endVerified = true;
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await base.DisposeAsync();
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
