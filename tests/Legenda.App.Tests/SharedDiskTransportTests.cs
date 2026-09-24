using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Legenda.App;
using Xunit;

namespace Legenda.App.Tests;

public sealed class SharedDiskTransportTests
{
    [Fact]
    public async Task DiskUsesImmutableUploadsPaginationAndPrivateDownloadLinks()
    {
        using var handler=new Handler();using var http=new HttpClient(handler);
        var store=new YandexSharedDatabaseStore(http);
        var versions=await store.ListAsync("test-token",CancellationToken.None);
        Assert.Single(versions);Assert.Equal(2,handler.CreatedFolders);
        var source=Path.GetTempFileName();var target=Path.GetTempFileName();
        try
        {
            File.WriteAllText(source,"SQLite snapshot test payload");
            await store.UploadAsync("test-token",versions[0],source,CancellationToken.None);
            await store.DownloadAsync("test-token",versions[0],target,CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(source),File.ReadAllBytes(target));
            await store.DeleteAsync("test-token",versions[0],CancellationToken.None);
            Assert.True(handler.Deleted);
        }
        finally {File.Delete(source);File.Delete(target);}
    }

    private sealed class Handler:HttpMessageHandler
    {
        public int CreatedFolders {get;private set;}
        public bool Deleted {get;private set;}
        private byte[] _data=Array.Empty<byte>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri!.Host=="transfer.yandex.net")
            {
                Assert.Null(request.Headers.Authorization);
                if(request.Method==HttpMethod.Put){_data=await request.Content!.ReadAsByteArrayAsync(ct);return new(HttpStatusCode.Created);}
                return new(HttpStatusCode.OK){Content=new ByteArrayContent(_data)};
            }
            Assert.Equal("cloud-api.yandex.net",request.RequestUri.Host);
            Assert.Equal("OAuth",request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token",request.Headers.Authorization.Parameter);
            if(request.Method==HttpMethod.Put){CreatedFolders++;return new(HttpStatusCode.Created);}
            if(request.Method==HttpMethod.Delete){Assert.Contains("permanently=true",request.RequestUri.Query);Deleted=true;return new(HttpStatusCode.NoContent);}
            if(request.RequestUri.AbsolutePath.EndsWith("/upload"))
            {Assert.Contains("overwrite=false",request.RequestUri.Query);return Json(new{href="https://transfer.yandex.net/upload"});}
            if(request.RequestUri.AbsolutePath.EndsWith("/download"))return Json(new{href="https://transfer.yandex.net/download"});
            if(CreatedFolders==0)return new(HttpStatusCode.NotFound);
            if(request.RequestUri.Query.Contains("offset=0"))return Json(new{_embedded=new{items=Enumerable.Range(0,100).Select(i=>new{name="unrelated-"+i,type="file"})}});
            Assert.Contains("offset=100",request.RequestUri.Query);
            return Json(new{_embedded=new{items=new[]{new{name=new DatabaseVersion(1234567890123,"0123456789abcdef0123456789abcdef").FileName,type="file"}}}});
        }
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    }
}
