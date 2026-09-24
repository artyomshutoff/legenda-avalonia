using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Legenda.App;

public sealed class YandexSharedDatabaseStore : ISharedDatabaseStore
{
    public const string Folder="disk:/Легенда/Общая база";
    private const string Api="https://cloud-api.yandex.net/v1/disk/resources";
    private static readonly HttpClient SharedClient=new() {Timeout=TimeSpan.FromMinutes(2)};
    private static readonly Regex NamePattern=new(@"^shared-(\d{16})-([a-f0-9]{32})\.db$",RegexOptions.CultureInvariant);
    private readonly HttpClient _http;
    public YandexSharedDatabaseStore(HttpClient? http=null)=>_http=http??SharedClient;
    private static string PathQuery(DatabaseVersion version)=>Uri.EscapeDataString(Folder+"/"+version.FileName);

    public async Task<IReadOnlyList<DatabaseVersion>> ListAsync(string token,CancellationToken ct)
    {
        var versions=new List<DatabaseVersion>();
        var createdFolder=false;
        for(var offset=0;;offset+=100)
        {
            using var response=await Send(HttpMethod.Get,Api+"?path="+Uri.EscapeDataString(Folder)+"&limit=100&offset="+offset,token,ct);
            if(response.StatusCode==HttpStatusCode.NotFound && !createdFolder)
            {
                foreach(var folder in new[]{"disk:/Легенда",Folder})
                {
                    using var created=await Send(HttpMethod.Put,Api+"?path="+Uri.EscapeDataString(folder),token,ct);
                    if(created.StatusCode!=HttpStatusCode.Conflict)Check(created);
                }
                createdFolder=true;offset-=100;continue;
            }
            Check(response);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items=json.RootElement.GetProperty("_embedded").GetProperty("items");
            foreach(var item in items.EnumerateArray())
            {
                if(item.GetProperty("type").GetString()!="file")continue;
                var match=NamePattern.Match(item.GetProperty("name").GetString()??"");
                if(match.Success && long.TryParse(match.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var modified))
                    versions.Add(new(modified,match.Groups[2].Value));
            }
            if(items.GetArrayLength()<100)return versions;
        }
    }

    public async Task UploadAsync(string token,DatabaseVersion version,string file,CancellationToken ct)
    {
        using var response=await Send(HttpMethod.Get,Api+"/upload?path="+PathQuery(version)+"&overwrite=false",token,ct);
        if(response.StatusCode==HttpStatusCode.Conflict)return;
        Check(response);
        var link=await ReadLink(response,ct);
        await using var input=File.OpenRead(file);
        using var request=new HttpRequestMessage(HttpMethod.Put,link){Content=new StreamContent(input)};
        using var result=await _http.SendAsync(request,ct);
        if(result.StatusCode!=HttpStatusCode.Conflict)Check(result);
    }

    public async Task DownloadAsync(string token,DatabaseVersion version,string file,CancellationToken ct)
    {
        using var response=await Send(HttpMethod.Get,Api+"/download?path="+PathQuery(version),token,ct);
        Check(response);
        var link=await ReadLink(response,ct);
        using var result=await _http.GetAsync(link,HttpCompletionOption.ResponseHeadersRead,ct);
        Check(result);
        await using var output=File.Create(file);
        await result.Content.CopyToAsync(output,ct);
    }

    public async Task DeleteAsync(string token,DatabaseVersion version,CancellationToken ct)
    {
        using var response=await Send(HttpMethod.Delete,Api+"?path="+PathQuery(version)+"&permanently=true",token,ct);
        if(response.StatusCode==HttpStatusCode.NotFound)return;
        Check(response);
        if(response.StatusCode!=HttpStatusCode.Accepted)return;
        var link=await ReadLink(response,ct);
        if(link.Host!="cloud-api.yandex.net")throw new InvalidOperationException("Некорректный адрес операции Диска.");
        for(var attempt=0;attempt<30;attempt++)
        {
            await Task.Delay(1000,ct);
            using var progress=await Send(HttpMethod.Get,link.ToString(),token,ct);
            Check(progress);
            using var state=JsonDocument.Parse(await progress.Content.ReadAsStringAsync(ct));
            var value=state.RootElement.GetProperty("status").GetString();
            if(value=="success")return;
            if(value=="failed")break;
        }
        throw new InvalidOperationException("Старые версии пока не удалены. Очистка повторится автоматически.");
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method,string uri,string token,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(method,uri);
        request.Headers.Authorization=new AuthenticationHeaderValue("OAuth",token);
        return await _http.SendAsync(request,ct);
    }
    private static async Task<Uri> ReadLink(HttpResponseMessage response,CancellationToken ct)
    {
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var uri=new Uri(json.RootElement.GetProperty("href").GetString()!);
        if(uri.Scheme!="https")throw new InvalidOperationException("Диск вернул небезопасную ссылку.");
        return uri;
    }
    private static void Check(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        throw new InvalidOperationException(response.StatusCode switch {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"Проверьте токен Яндекс Диска и права чтения и записи.",
            HttpStatusCode.InsufficientStorage=>"На Яндекс Диске недостаточно места.",
            _=>$"Яндекс Диск вернул ошибку {(int)response.StatusCode}."
        });
    }
}
