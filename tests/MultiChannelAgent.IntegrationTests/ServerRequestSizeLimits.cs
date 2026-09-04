using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>What one request's own body-size limit turned out to be, and what the server did with it.</summary>
/// <param name="Path">The request path, so a scenario can tell its upload from its sign-in.</param>
/// <param name="AppliedLimit">
/// The limit in force by the time the body was read. It is null unless the matched endpoint carried
/// <c>IRequestSizeLimitMetadata</c>, because this harness starts every request unlimited: a non-null
/// value is therefore the route's own metadata, applied by ASP.NET Core's endpoint routing.
/// </param>
/// <param name="BytesDelivered">How much of the body the application was allowed to read.</param>
/// <param name="RefusedByTheServer">Whether the body was refused for exceeding <paramref name="AppliedLimit"/>.</param>
public sealed record ServerRequestSizeLimitRecord(
    string Path,
    long? AppliedLimit,
    long BytesDelivered,
    bool RefusedByTheServer);

/// <summary>What <see cref="ServerRequestSizeLimitExtensions"/> observed, one entry per request.</summary>
public sealed class ServerRequestSizeLimitLog
{
    private readonly List<ServerRequestSizeLimitRecord> _records = [];

    public IReadOnlyList<ServerRequestSizeLimitRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    internal void Add(ServerRequestSizeLimitRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }
}

/// <summary>
/// Gives the test server the one thing it does not implement.
///
/// A per-route body bound is declared as endpoint metadata and applied by ASP.NET Core's endpoint
/// routing to <see cref="IHttpMaxRequestBodySizeFeature"/> - which is a *server* feature. Kestrel
/// implements it; <c>Microsoft.AspNetCore.TestHost</c> does not, so under the ordinary test server the
/// framework logs that the limit could not be applied and every body is accepted whatever the route
/// says. Nothing about a route's declared bound can be proven that way.
///
/// This supplies the missing feature and enforces it exactly where Kestrel does - when the body is
/// first read, against the declared content length - so a scenario can prove that the route's own
/// bound reaches the server and that a body beyond it is refused before the endpoint receives any of
/// it. It proves the wiring and the refusal; it is not Kestrel, and it is not evidence about how any
/// other server behaves.
/// </summary>
public static class ServerRequestSizeLimitExtensions
{
    public static IServiceCollection AddServerEnforcedRequestBodySizeLimit(
        this IServiceCollection services, ServerRequestSizeLimitLog log)
    {
        services.AddSingleton<IStartupFilter>(new RequestBodySizeLimitStartupFilter(log));
        return services;
    }

    private sealed class RequestBodySizeLimitStartupFilter(ServerRequestSizeLimitLog log) : IStartupFilter
    {
        // Registered ahead of the application's own pipeline, because endpoint routing - which is what
        // maps the route's metadata onto the feature - must find the feature already there.
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, continuation) =>
            {
                var feature = new MaxRequestBodySizeFeature();
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

                var body = new BoundedRequestBody(context.Request.Body, feature, context.Request.ContentLength);
                context.Request.Body = body;

                try
                {
                    await continuation(context);
                }
                catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
                {
                    // What Kestrel does with its own refusal: answer with the status it carries, and
                    // never let the endpoint write an answer of its own.
                    context.Response.Clear();
                    context.Response.StatusCode = exception.StatusCode;
                }
                finally
                {
                    log.Add(new ServerRequestSizeLimitRecord(
                        context.Request.Path,
                        feature.MaxRequestBodySize,
                        body.BytesDelivered,
                        body.RefusedByTheServer));
                }
            });

            next(builder);
        };
    }

    private sealed class MaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        private long? _maxRequestBodySize;

        public bool IsReadOnly { get; private set; }

        public long? MaxRequestBodySize
        {
            get => _maxRequestBodySize;
            set
            {
                if (IsReadOnly)
                {
                    throw new InvalidOperationException(
                        "The max request body size cannot be modified after the app has started reading the request body.");
                }

                _maxRequestBodySize = value;
            }
        }

        internal void Freeze() => IsReadOnly = true;
    }

    /// <summary>
    /// The request body as a bounded server hands it over: the declared length is checked the moment
    /// the application starts reading, and a body without one is measured as it arrives, so nothing
    /// past the bound is ever delivered.
    /// </summary>
    private sealed class BoundedRequestBody(Stream inner, MaxRequestBodySizeFeature feature, long? contentLength)
        : Stream
    {
        internal long BytesDelivered { get; private set; }

        internal bool RefusedByTheServer { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            StartReading();
            var read = inner.Read(buffer, offset, count);
            Delivered(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            StartReading();
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Delivered(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void StartReading()
        {
            feature.Freeze();

            if (feature.MaxRequestBodySize is { } limit && contentLength > limit)
            {
                Refuse(limit);
            }
        }

        private void Delivered(int read)
        {
            BytesDelivered += read;

            if (feature.MaxRequestBodySize is { } limit && BytesDelivered > limit)
            {
                Refuse(limit);
            }
        }

        private void Refuse(long limit)
        {
            RefusedByTheServer = true;
            throw new BadHttpRequestException(
                $"Request body too large. The max request body size is {limit} bytes.",
                StatusCodes.Status413PayloadTooLarge);
        }
    }
}
