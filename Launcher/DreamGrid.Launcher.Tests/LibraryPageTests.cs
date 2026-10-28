using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LibraryPageTests : IAsyncLifetime
{
    private const long MB = 1L << 20;

    private readonly LauncherFactory _f;

    /// <summary>Set by the upload probe once the server has finished an upload request.</summary>
    private readonly TaskCompletionSource _uploadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True when an upload request's form was read (ReadFormAsync ran), which would buffer the whole file.</summary>
    private volatile bool _formRead;

    public LibraryPageTests()
    {
        _f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
        })
        {
            Before = app =>
            {
                TestAuth.Apply(app);
                app.Use(UploadProbe);
            },
        };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// On upload requests: a body limit that behaves like Kestrel's (30 MB, 413 when exceeded, fixed once reading starts),
    /// since the TestServer has none; afterwards it notes whether the form was read.
    /// </summary>
    private async Task UploadProbe(HttpContext ctx, RequestDelegate next)
    {
        if (!ctx.Request.Path.StartsWithSegments("/regions/library/upload"))
        {
            await next(ctx);
            return;
        }
        var limited = new KestrelLikeBody(ctx.Request.Body);
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(limited);
        ctx.Request.Body = limited;
        try
        {
            await next(ctx);
        }
        finally
        {
            if (ctx.Features.Get<IFormFeature>() is { Form: not null }) _formRead = true;
            _uploadDone.TrySetResult();
        }
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static async Task<AngleSharp.Html.Dom.IHtmlDocument> PageAsync(HttpClient client, string url = "/regions/library") =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var page = await PageAsync(client);
        var token = page.QuerySelector("form#upload input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> PostPageAsync(HttpClient client, string handler, Dictionary<string, string> fields)
    {
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = await TokenAsync(client) };
        return await client.PostAsync("/regions/library?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    /// <summary>A small real OAR: a gzip tar whose first entry is archive.xml.</summary>
    private static byte[] OarBytes()
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "archive.xml") { DataStream = new MemoryStream("<archive/>"u8.ToArray()) });
        return ms.ToArray();
    }

    private string LibraryFile(string name) => Path.Combine(_f.Paths.Oars, name);

    private void PutInLibrary(string name, byte[]? bytes = null)
    {
        Directory.CreateDirectory(_f.Paths.Oars);
        File.WriteAllBytes(LibraryFile(name), bytes ?? OarBytes());
    }

    private void SeedTypes(params CustomRegionType[] types) =>
        _f.Store.Save(_f.Store.LoadOrCreate() with { CustomTypes = [.. types] });

    private static ByteArrayContent FilePart(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    /// <summary>The upload as JavaScript sends it: the token in the header, the file in the body.</summary>
    private static async Task<HttpResponseMessage> FetchUploadAsync(HttpClient client, string token, string fileName, HttpContent file,
        CancellationToken? ct = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/regions/library/upload");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "fetch");
        request.Content = new MultipartFormDataContent { { file, "oar", fileName } };
        return await client.SendAsync(request, ct ?? Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private string[] IncomingFiles() =>
        Directory.Exists(_f.Paths.OarsIncoming) ? Directory.GetFiles(_f.Paths.OarsIncoming) : [];

    private string[] LibraryFiles() =>
        Directory.Exists(_f.Paths.Oars) ? Directory.GetFiles(_f.Paths.Oars).Select(p => Path.GetFileName(p)).ToArray() : [];

    [Fact]
    public async Task Library_lists_files_with_size_and_date()
    {
        Directory.CreateDirectory(_f.Paths.Oars);
        using (var big = File.Create(LibraryFile("castle.oar"))) big.SetLength(1 * MB);
        PutInLibrary("Beach house.oar");
        var when = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LibraryFile("castle.oar"), when);
        using var client = await SignedInAsync();

        var doc = await PageAsync(client);

        var rows = doc.QuerySelectorAll("#oar-list tbody tr");
        Assert.Equal(["Beach house.oar", "castle.oar"], rows.Select(r => r.QuerySelector(".oar-name")!.TextContent.Trim()));
        var castle = rows[1];
        Assert.Equal("1.0 MB", castle.QuerySelector(".oar-size")!.TextContent.Trim());
        Assert.Equal(new DateTimeOffset(when).ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture),
            castle.QuerySelector(".oar-date")!.TextContent.Trim());
        Assert.Equal("/regions/library/file/castle.oar", castle.QuerySelector("a.download")!.GetAttribute("href"));
        Assert.Equal("/regions/library/file/Beach%20house.oar", rows[0].QuerySelector("a.download")!.GetAttribute("href"));
        Assert.Contains($"Very large files can also be copied straight into {_f.Paths.Oars}, for example over SFTP.",
            doc.QuerySelector(".library-path-note")!.TextContent);
        var text = doc.Body!.TextContent;
        Assert.DoesNotContain("program", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instance", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_form_works_without_javascript_and_puts_the_token_first()
    {
        using var client = await SignedInAsync();

        var form = (await PageAsync(client)).QuerySelector("form#upload")!;

        Assert.Equal("/regions/library/upload", form.GetAttribute("action"));
        Assert.Equal("multipart/form-data", form.GetAttribute("enctype"));
        var inputs = form.QuerySelectorAll("input").Select(i => i.GetAttribute("name")).ToList();
        Assert.Equal("__RequestVerificationToken", inputs[0]);
        Assert.Equal(1, inputs.Count(n => n == "__RequestVerificationToken"));
        Assert.Contains("oar", inputs);
    }

    [Fact]
    public async Task Download_sends_the_file_as_an_attachment_with_ranges()
    {
        var bytes = OarBytes();
        PutInLibrary("castle.oar", bytes);
        using var client = await SignedInAsync();

        using var response = await client.GetAsync("/regions/library/file/castle.oar", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("castle.oar", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Library_needs_admin()
    {
        PutInLibrary("castle.oar");
        using var anonymous = _f.CreateClient(new() { AllowAutoRedirect = false });

        using var page = await anonymous.GetAsync("/regions/library", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/login", page.Headers.Location!.OriginalString.Replace("http://localhost", ""));

        using var file = await anonymous.GetAsync("/regions/library/file/castle.oar", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, file.StatusCode);

        using var upload = await anonymous.PostAsync("/regions/library/upload",
            new MultipartFormDataContent { { FilePart(OarBytes()), "oar", "x.oar" } }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(["castle.oar"], LibraryFiles());
    }

    [Fact]
    public async Task Upload_with_header_token_saves_the_oar()
    {
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);
        var bytes = OarBytes();

        using var response = await FetchUploadAsync(client, token, "castle.oar", FilePart(bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("castle.oar", json.GetProperty("name").GetString());
        Assert.Equal("Uploaded castle.oar.", json.GetProperty("text").GetString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(LibraryFile("castle.oar"), Ct));
        Assert.Empty(IncomingFiles());
        Assert.False(_formRead);
    }

    [Fact]
    public async Task Upload_without_javascript_form_works()
    {
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);
        // As a browser sends the form: the hidden token field first, then the file.
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { FilePart(OarBytes()), "oar", "castle.oar" },
        };

        using var response = await client.PostAsync("/regions/library/upload", content, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/regions/library", response.Headers.Location!.OriginalString);
        Assert.True(File.Exists(LibraryFile("castle.oar")));
        var doc = await PageAsync(client);
        Assert.Equal("Uploaded castle.oar.", doc.QuerySelector(".notice")!.TextContent.Trim());
        Assert.False(_formRead);
    }

    [Fact]
    public async Task Upload_without_a_token_is_refused()
    {
        using var client = await SignedInAsync();
        await TokenAsync(client); // the antiforgery cookie, but no token in the request

        using var response = await client.PostAsync("/regions/library/upload",
            new MultipartFormDataContent { { FilePart(OarBytes()), "oar", "castle.oar" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(LibraryFiles());
        Assert.Empty(IncomingFiles());
    }

    [Fact]
    public async Task Upload_streams_without_buffering()
    {
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);

        // 64 MB, over Kestrel's 30 MB default, made as it is sent.
        using var response = await FetchUploadAsync(client, token, "big.oar", new GeneratedOar(64 * MB));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("ok").GetBoolean());
        Assert.True(new FileInfo(LibraryFile("big.oar")).Length > 64 * MB);
        Assert.False(_formRead);
    }

    [Fact]
    public async Task Cut_off_upload_leaves_no_file()
    {
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);

        // The TestServer never completes the response of a request whose body failed, so the client gives up on its own;
        // the probe says when the server has finished with it.
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        giveUp.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await FetchUploadAsync(client, token, "castle.oar", new GeneratedOar(16 * MB, failAfter: 4 * MB), giveUp.Token);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { }
        await _uploadDone.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Empty(LibraryFiles());
        Assert.Empty(IncomingFiles());
    }

    [Fact]
    public async Task Upload_of_non_oar_is_refused()
    {
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);

        using var fetched = await FetchUploadAsync(client, token, "notes.oar", FilePart("just some notes"u8.ToArray()));

        var json = await JsonAsync(fetched);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("That file isn't an OAR.", json.GetProperty("text").GetString());

        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { FilePart("plain text"u8.ToArray()), "oar", "notes.txt" },
        };
        using var posted = await client.PostAsync("/regions/library/upload", content, Ct);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal("That file isn't an OAR.", (await PageAsync(client)).QuerySelector(".notice")!.TextContent.Trim());

        Assert.Empty(LibraryFiles());
        Assert.Empty(IncomingFiles());
    }

    [Fact]
    public async Task Upload_with_a_taken_name_gets_a_number()
    {
        PutInLibrary("castle.oar");
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);

        using var response = await FetchUploadAsync(client, token, "castle.oar", FilePart(OarBytes()));

        var json = await JsonAsync(response);
        Assert.Equal("castle-2.oar", json.GetProperty("name").GetString());
        Assert.Equal("Uploaded castle-2.oar.", json.GetProperty("text").GetString());
        Assert.Equal(["castle-2.oar", "castle.oar"], LibraryFiles().Order());
    }

    [Fact]
    public async Task Upload_cleans_the_browsers_file_name()
    {
        using var client = await SignedInAsync();
        var token = await TokenAsync(client);

        using var response = await FetchUploadAsync(client, token, "..\\..\\my<castle>", FilePart(OarBytes()));

        Assert.Equal("my_castle_.oar", (await JsonAsync(response)).GetProperty("name").GetString());
        Assert.Equal(["my_castle_.oar"], LibraryFiles());
    }

    [Theory]
    [InlineData("..%2Fsettings.json")]
    [InlineData(".hidden.oar")]
    [InlineData("x.txt")]
    public async Task Library_endpoints_refuse_unsafe_names(string name)
    {
        PutInLibrary(".hidden.oar");
        PutInLibrary("x.txt");
        using var client = await SignedInAsync();
        var plain = Uri.UnescapeDataString(name);

        using var download = await client.GetAsync("/regions/library/file/" + name, Ct);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);

        using var delete = await PostPageAsync(client, "delete", new() { ["name"] = plain, ["confirmed"] = "true" });
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        using var useAsType = await PostPageAsync(client, "useAsType", new() { ["name"] = plain, ["typeName"] = "Sneaky" });
        Assert.Equal(HttpStatusCode.NotFound, useAsType.StatusCode);

        Assert.True(File.Exists(_f.Paths.SettingsFile));
        Assert.True(File.Exists(LibraryFile(".hidden.oar")));
        Assert.True(File.Exists(LibraryFile("x.txt")));
        Assert.Empty(_f.Store.LoadOrCreate().CustomTypes);
    }

    [Fact]
    public async Task Delete_asks_first_and_names_its_types()
    {
        PutInLibrary("castle.oar");
        SeedTypes(new CustomRegionType("c-0000abcd", "My Castle", "castle.oar"));
        using var client = await SignedInAsync();

        using var ask = await PostPageAsync(client, "delete", new() { ["name"] = "castle.oar" });

        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var doc = new HtmlParser().ParseDocument(await ask.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Delete castle.oar? This can't be undone. It is also the region type My Castle, which goes too.",
            doc.QuerySelector(".delete-question")!.TextContent.Trim());
        Assert.True(File.Exists(LibraryFile("castle.oar")));

        using var yes = await PostPageAsync(client, "delete", new() { ["name"] = "castle.oar", ["confirmed"] = "true" });

        Assert.Equal(HttpStatusCode.Redirect, yes.StatusCode);
        Assert.False(File.Exists(LibraryFile("castle.oar")));
        Assert.Empty(_f.Store.LoadOrCreate().CustomTypes);
        Assert.Contains("UpdateSettings", _f.Grid.Snapshot());
    }

    [Fact]
    public async Task Delete_of_a_file_without_types_asks_plainly()
    {
        PutInLibrary("castle.oar");
        using var client = await SignedInAsync();

        using var ask = await PostPageAsync(client, "delete", new() { ["name"] = "castle.oar" });

        var doc = new HtmlParser().ParseDocument(await ask.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Delete castle.oar? This can't be undone.", doc.QuerySelector(".delete-question")!.TextContent.Trim());
    }

    [Fact]
    public async Task Use_as_type_adds_it_and_refuses_a_taken_name()
    {
        PutInLibrary("castle.oar");
        using var client = await SignedInAsync();

        using var added = await PostPageAsync(client, "useAsType", new() { ["name"] = "castle.oar", ["typeName"] = " My Castle " });

        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        var type = Assert.Single(_f.Store.LoadOrCreate().CustomTypes);
        Assert.Equal("My Castle", type.Name);
        Assert.Equal("castle.oar", type.File);
        Assert.True(RegionTypes.IsCustom(type.Id));
        Assert.Contains("UpdateSettings", _f.Grid.Snapshot());
        var page = await PageAsync(client);
        Assert.Contains("My Castle", page.QuerySelector("#custom-types")!.TextContent);

        foreach (var (wanted, error) in new[]
                 {
                     ("meadow", "There is already a region type called Meadow."),
                     ("MY CASTLE", "There is already a region type called My Castle."),
                     ("A", "A region type's name has 2 to 32 characters."),
                     (new string('x', 33), "A region type's name has 2 to 32 characters."),
                 })
        {
            using var refused = await PostPageAsync(client, "useAsType", new() { ["name"] = "castle.oar", ["typeName"] = wanted });
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var doc = new HtmlParser().ParseDocument(await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal(error, doc.QuerySelector(".type-error")!.TextContent.Trim());
        }
        Assert.Single(_f.Store.LoadOrCreate().CustomTypes);
    }

    [Fact]
    public async Task Remove_type_keeps_the_file()
    {
        PutInLibrary("castle.oar");
        SeedTypes(new CustomRegionType("c-0000abcd", "My Castle", "castle.oar"));
        using var client = await SignedInAsync();
        var page = await PageAsync(client);
        Assert.Contains("The file stays in the library.", page.QuerySelector("#custom-types")!.TextContent);

        using var response = await PostPageAsync(client, "removeType", new() { ["id"] = "c-0000abcd" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(_f.Store.LoadOrCreate().CustomTypes);
        Assert.True(File.Exists(LibraryFile("castle.oar")));

        using var unknown = await PostPageAsync(client, "removeType", new() { ["id"] = "island" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Regions_page_links_the_library()
    {
        using var client = await SignedInAsync();

        var doc = await PageAsync(client, "/regions");

        Assert.Equal("OAR library", doc.QuerySelector("a[href='/regions/library']")!.TextContent.Trim());
    }

    /// <summary>A request body limited like Kestrel's: 413 past the limit, and the limit is fixed once reading starts.</summary>
    private sealed class KestrelLikeBody(Stream inner) : Stream, IHttpMaxRequestBodySizeFeature
    {
        private long? _max = 30_000_000;
        private long _read;

        public bool IsReadOnly => _read > 0;

        public long? MaxRequestBodySize
        {
            get => _max;
            set => _max = IsReadOnly ? throw new InvalidOperationException("The request body is already being read.") : value;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        private int Count(int n)
        {
            _read += n;
            if (_read > _max) throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A real OAR of about <c>bytes</c>, stored without compression and made while it is sent; it can fail partway.</summary>
    private sealed class GeneratedOar : HttpContent
    {
        private readonly long _bytes;
        private readonly long? _failAfter;

        public GeneratedOar(long bytes, long? failAfter = null)
        {
            (_bytes, _failAfter) = (bytes, failAfter);
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var gzip = new GZipStream(stream, CompressionLevel.NoCompression, leaveOpen: true);
            await using var tar = new TarWriter(gzip, leaveOpen: true);
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "archive.xml") { DataStream = new MemoryStream("<archive/>"u8.ToArray()) });
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "assets/filler.bin") { DataStream = new Zeros(_bytes, _failAfter) });
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A seekable stream of zeros that throws an <see cref="IOException"/> after <c>failAfter</c> bytes.</summary>
    private sealed class Zeros(long length, long? failAfter) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAfter) throw new IOException("The connection was lost.");
            var n = (int)Math.Min(count, length - Position);
            Array.Clear(buffer, offset, n);
            Position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
