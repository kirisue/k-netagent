using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task Parses_reasoning_content_and_usage()
    {
        const string s = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"r\",\"content\":\"ok\"}}]}\n\n" +
                         "data: {\"choices\":[],\"usage\":{\"total_tokens\":7}}\n\n" +
                         "data: [DONE]\n\n";
        var provider = new OpenAiCompatibleProvider(new HttpClient(new Handler(s)));
        var list = new List<StreamEvent>();
        await foreach (var item in provider.StreamAsync(new([], new("custom", "http://fake/v1", "m"), null), CancellationToken.None)) list.Add(item);
        Assert.Contains(list, x => x.Kind == StreamEventKind.Reasoning && x.Text == "r");
        Assert.Contains(list, x => x.Kind == StreamEventKind.Content && x.Text == "ok");
        Assert.Contains(list, x => x.Tokens == 7);
    }

    [Fact]
    public async Task Reassembles_streamed_tool_call_arguments()
    {
        const string s = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"read_\",\"arguments\":\"{\\\"pa\"}}]}}]}\n\n" +
                         "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"file\",\"arguments\":\"th\\\":\\\"README.md\\\"}\"}}]}}]}\n\n" +
                         "data: [DONE]\n\n";
        var provider = new OpenAiCompatibleProvider(new HttpClient(new Handler(s)));
        var list = new List<StreamEvent>();
        await foreach (var item in provider.StreamAsync(
                           new([], new("custom", "http://fake/v1", "m"), null,
                               [new("read_file", "read", ToolRiskLevel.ReadOnly, [])]), CancellationToken.None)) list.Add(item);
        var call = Assert.Single(list, x => x.ToolCall is not null).ToolCall!;
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task Keeps_distinct_streamed_tool_calls_when_provider_omits_index()
    {
        const string s = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-a\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"A.md\\\"}\"}},{\"id\":\"call-b\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"B.md\\\"}\"}}]}}]}\n\n" +
                         "data: [DONE]\n\n";
        var provider = new OpenAiCompatibleProvider(new HttpClient(new Handler(s)));
        var list = new List<StreamEvent>();
        await foreach (var item in provider.StreamAsync(new([], new("custom", "http://fake/v1", "m"), null), CancellationToken.None)) list.Add(item);
        var calls = list.Where(x => x.ToolCall is not null).Select(x => x.ToolCall!).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(["call-a", "call-b"], calls.Select(x => x.Id));
        Assert.Equal(["{\"path\":\"A.md\"}", "{\"path\":\"B.md\"}"], calls.Select(x => x.ArgumentsJson));
    }

    [Fact]
    public async Task Does_not_duplicate_repeated_complete_function_name()
    {
        const string s = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"pa\"}}]}}]}\n\n" +
                         "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"th\\\":\\\"README.md\\\"}\"}}]}}]}\n\n" +
                         "data: [DONE]\n\n";
        var provider = new OpenAiCompatibleProvider(new HttpClient(new Handler(s)));
        var list = new List<StreamEvent>();
        await foreach (var item in provider.StreamAsync(new([], new("custom", "http://fake/v1", "m"), null), CancellationToken.None)) list.Add(item);
        var call = Assert.Single(list, x => x.ToolCall is not null).ToolCall!;
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task Serializes_object_arguments_in_streamed_tool_call()
    {
        const string s = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"README.md\",\"startLine\":2}}}]}}]}\n\n" +
                         "data: [DONE]\n\n";
        var provider = new OpenAiCompatibleProvider(new HttpClient(new Handler(s)));
        var list = new List<StreamEvent>();
        await foreach (var item in provider.StreamAsync(new([], new("custom", "http://fake/v1", "m"), null), CancellationToken.None)) list.Add(item);
        var call = Assert.Single(list, x => x.ToolCall is not null).ToolCall!;
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("README.md", arguments.RootElement.GetProperty("path").GetString());
        Assert.Equal(2, arguments.RootElement.GetProperty("startLine").GetInt32());
    }

    [Fact]
    public async Task Parses_non_streaming_compatible_response()
    {
        const string json="""{"choices":[{"message":{"reasoning_content":"r","content":"answer","tool_calls":[{"id":"c1","function":{"name":"read_file","arguments":"{\"path\":\"README.md\"}"}}]}}],"usage":{"total_tokens":9}}""";
        var provider=new OpenAiCompatibleProvider(new HttpClient(new Handler(json,"application/json")));var list=new List<StreamEvent>();
        await foreach(var item in provider.StreamAsync(new([],new("custom","http://fake/v1","m"),null),CancellationToken.None))list.Add(item);
        Assert.Contains(list,x=>x.Kind==StreamEventKind.Content&&x.Text=="answer");Assert.Contains(list,x=>x.Kind==StreamEventKind.Reasoning&&x.Text=="r");
        Assert.Contains(list,x=>x.ToolCall?.Name=="read_file");Assert.Contains(list,x=>x.Tokens==9);
    }

    [Fact]
    public async Task Tool_schema_omits_null_enum_and_includes_usage_example()
    {
        string? body=null;var provider=new OpenAiCompatibleProvider(new HttpClient(new CapturingHandler(x=>body=x)));
        await foreach(var _ in provider.StreamAsync(new([],new("custom","http://fake/v1","m"),null,[new("read_file","Read lines",ToolRiskLevel.ReadOnly,[new("path","string","File",true)],"{\"path\":\"README.md\"}")]),CancellationToken.None)){}
        using var doc=JsonDocument.Parse(body!);var function=doc.RootElement.GetProperty("tools")[0].GetProperty("function");Assert.Contains("Example:",function.GetProperty("description").GetString());var path=function.GetProperty("parameters").GetProperty("properties").GetProperty("path");Assert.False(path.TryGetProperty("enum",out _));
    }

    [Fact]
    public async Task Serializes_sanitized_image_only_in_current_request_content_array()
    {
        string? body=null;var bytes=OnePixelPng();var image=new ImageInput("image/png",bytes,Convert.ToHexString(SHA256.HashData(bytes)),1,1);
        var provider=new OpenAiCompatibleProvider(new HttpClient(new CapturingHandler(x=>body=x)));
        var settings=new ProviderSettings("custom","https://model.example/v1","vision",SelfReviewEnabled:false,SupportsImageInput:true);
        await foreach(var _ in provider.StreamAsync(new([new(ChatRole.User,"analyze",DateTimeOffset.UtcNow)],settings,null,Images:[image]),CancellationToken.None)){}
        using var doc=JsonDocument.Parse(body!);var message=doc.RootElement.GetProperty("messages")[0];var content=message.GetProperty("content");
        Assert.Equal("text",content[0].GetProperty("type").GetString());Assert.Equal("analyze",content[0].GetProperty("text").GetString());
        var url=content[1].GetProperty("image_url").GetProperty("url").GetString();Assert.Equal("data:image/png;base64,"+Convert.ToBase64String(bytes),url);
        Assert.DoesNotContain(image.Sha256,body,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("file",body,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_or_insecure_image_input_is_rejected_before_http()
    {
        var calls=0;var bytes=new byte[]{1};var image=new ImageInput("image/png",bytes,Convert.ToHexString(SHA256.HashData(bytes)),1,1);
        var provider=new OpenAiCompatibleProvider(new HttpClient(new CapturingHandler(_=>calls++)));
        async Task ReadAll(ProviderSettings settings){await foreach(var _ in provider.StreamAsync(new([new(ChatRole.User,"x",DateTimeOffset.UtcNow)],settings,null,Images:[image]),CancellationToken.None)){} }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAll(new("custom","https://model.example/v1","m",SupportsImageInput:false)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAll(new("custom","http://model.example/v1","m",SupportsImageInput:true)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAll(new("custom","https://model.example/v1?api_key=value","m",SupportsImageInput:true)));
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task Image_remains_on_user_message_after_a_closed_tool_exchange()
    {
        string? body=null;var bytes=OnePixelPng();var image=new ImageInput("image/png",bytes,Convert.ToHexString(SHA256.HashData(bytes)),1,1);var now=DateTimeOffset.UtcNow;
        var messages=new ChatMessage[]{new(ChatRole.User,"inspect",now),new(ChatRole.Assistant,"",now,ToolCalls:[new("call-1","read_file","{}")]),new(ChatRole.Tool,"result",now,"call-1","read_file")};
        var provider=new OpenAiCompatibleProvider(new HttpClient(new CapturingHandler(x=>body=x)));var settings=new ProviderSettings("custom","https://model.example/v1","vision",SupportsImageInput:true);
        await foreach(var _ in provider.StreamAsync(new(messages,settings,null,Images:[image]),CancellationToken.None)){}
        using var document=JsonDocument.Parse(body!);var wire=document.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array,wire[0].GetProperty("content").ValueKind);
        Assert.Equal("data:image/png;base64,"+Convert.ToBase64String(bytes),wire[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("assistant",wire[1].GetProperty("role").GetString());Assert.Equal("call-1",wire[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool",wire[2].GetProperty("role").GetString());Assert.Equal("call-1",wire[2].GetProperty("tool_call_id").GetString());Assert.Equal(JsonValueKind.String,wire[2].GetProperty("content").ValueKind);
        Assert.Equal(1,body!.Split("data:image/png;base64,",StringSplitOptions.None).Length-1);
    }

    [Fact]
    public async Task Provider_rejects_mime_spoof_and_dimension_mismatch_before_http()
    {
        var calls=0;var bytes=OnePixelPng();var settings=new ProviderSettings("custom","https://model.example/v1","vision",SupportsImageInput:true);
        var provider=new OpenAiCompatibleProvider(new HttpClient(new CapturingHandler(_=>calls++)));
        async Task Read(ImageInput image){await foreach(var _ in provider.StreamAsync(new([new(ChatRole.User,"x",DateTimeOffset.UtcNow)],settings,null,Images:[image]),CancellationToken.None)){} }
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new("image/jpeg",bytes,Convert.ToHexString(SHA256.HashData(bytes)),1,1)));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new("image/png",bytes,Convert.ToHexString(SHA256.HashData(bytes)),2,1)));
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task Image_request_is_attempted_once_on_transient_server_failure()
    {
        var handler=new StatusHandler(HttpStatusCode.InternalServerError);var bytes=OnePixelPng();
        var image=new ImageInput("image/png",bytes,Convert.ToHexString(SHA256.HashData(bytes)),1,1);
        var provider=new OpenAiCompatibleProvider(new HttpClient(handler));
        async Task Read(){await foreach(var _ in provider.StreamAsync(new([new(ChatRole.User,"x",DateTimeOffset.UtcNow)],new("custom","https://model.example/v1","vision",SupportsImageInput:true),null,Images:[image]),CancellationToken.None)){} }
        await Assert.ThrowsAsync<HttpRequestException>(Read);
        Assert.Equal(1,handler.Calls);
    }

    private sealed class Handler(string value,string mediaType="text/event-stream") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(value, Encoding.UTF8, mediaType) });
    }
    private sealed class CapturingHandler(Action<string> capture):HttpMessageHandler{protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){capture(await request.Content!.ReadAsStringAsync(ct));return new(HttpStatusCode.OK){Content=new StringContent("data: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};}}
    private sealed class StatusHandler(HttpStatusCode status):HttpMessageHandler{public int Calls{get;private set;}protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent("failed")});}}
    private static byte[] OnePixelPng()=>Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
