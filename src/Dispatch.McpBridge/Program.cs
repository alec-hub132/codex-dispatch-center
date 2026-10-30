using System.Text;
using System.Text.Json;
using Dispatch.Core;
using Dispatch.McpBridge;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var instance = Transport.Argument(args, "--instance", "default");
var module = Transport.Argument(args, "--module", "all");
var definitions = ToolCatalog.All(module);
_ = Transport.PipeName(instance);
var initialized = false;
while (true)
{
    JsonElement? id = null;
    object response;
    try
    {
        var line = await Transport.ReadLineAsync(Console.In, CancellationToken.None);
        if (line is null) break;
        using var message = JsonDocument.Parse(line);
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            throw new DispatchException("INVALID_REQUEST", "无效的 JSON-RPC 请求。");
        if (!root.TryGetProperty("id", out var requestId)) continue; // 通知不产生响应。
        id = requestId.Clone();
        if (id.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)) throw new DispatchException("INVALID_REQUEST", "无效请求编号。");
        var method = methodElement.GetString();
        if (method == "initialize")
        {
            if (initialized) throw new DispatchException("INVALID_REQUEST", "连接已经初始化。");
            var requestedProtocol = root.GetProperty("params").GetProperty("protocolVersion").GetString();
            var negotiatedProtocol = requestedProtocol is "2024-11-05" or "2025-03-26" or "2025-06-18" ? requestedProtocol : "2025-06-18";
            initialized = true;
            response = Result(new { protocolVersion = negotiatedProtocol, capabilities = new { tools = new { } },
                serverInfo = new { name = module == "queue" ? "codex-queue" : module == "models" ? "codex-model-dispatch" : "codex-dispatch", version = "0.11.3" },
                instructions = module == "queue" ? "这是测试排队工具。先get_module_settings核对排队是否启用，关闭不代表可绕过共享资源占用。已有许可必须清理释放。本工具不控制模型。" : module == "models" ? "这是模型调度工具。模型选择由已配置主管判断，工具不按阶段硬编码模型。先读取模块设置及排除名单；已有周期完成或执行中需要主管接管时均沿同周期返回主管。发送回执不代表生效。本工具不申请测试资源。" : "测试排队与模型调度独立启停；先get_module_settings核对当前模块设置。资源使用须有许可，已有占用必须清理。模型由主管规划并核验交接，执行结束或中途需要接管时沿原周期返回；不得把发送回执当作生效。" });
        }
        else if (method == "ping") response = Result(new { });
        else if (!initialized) response = Error(-32002, "请先初始化 MCP 连接。");
        else if (method == "tools/list") response = Result(new { tools = definitions });
        else if (method == "tools/call")
        {
            var parameters = root.GetProperty("params");
            var name = parameters.GetProperty("name").GetString() ?? throw new JsonException();
            var arguments = parameters.TryGetProperty("arguments", out var value) ? value : JsonSerializer.SerializeToElement(new { });
            var definition = definitions.Select(t => JsonSerializer.SerializeToElement(t)).SingleOrDefault(t => t.GetProperty("name").GetString() == name);
            if (definition.ValueKind == JsonValueKind.Undefined) throw new DispatchException("UNKNOWN_TOOL", "未知工具。");
            Validate(arguments, definition.GetProperty("inputSchema"));
            var reply = await Transport.Call(instance, name, arguments);
            var text = reply.Ok ? reply.Result!.Value.GetRawText() : JsonSerializer.Serialize(new { code = reply.Code, message = reply.Message }, Json.Options);
            response = Result(new { content = new[] { new { type = "text", text } }, isError = !reply.Ok });
        }
        else response = Error(-32601, "未知 MCP 方法。");
    }
    catch (JsonException) { response = Error(-32700, "JSON 格式无效。"); }
    catch (DispatchException ex) { response = Error(ex.Code == "INVALID_REQUEST" ? -32600 : -32602, ex.Message); }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
    { response = Error(-32602, "请求参数无效。"); }
    catch (Exception ex)
    {
        Console.Error.WriteLine("MCP 桥异常：" + ex);
        response = Error(-32603, "MCP 桥发生内部错误。");
    }
    Console.WriteLine(JsonSerializer.Serialize(response, Json.Options));
    object Result(object result) => new { jsonrpc = "2.0", id, result };
    object Error(int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
}

static void Validate(JsonElement value, JsonElement schema)
{
    if (value.ValueKind != JsonValueKind.Object) throw new DispatchException("INVALID_ARGUMENT", "参数必须是对象。");
    var properties = schema.GetProperty("properties");
    foreach (var required in schema.GetProperty("required").EnumerateArray())
        if (!value.TryGetProperty(required.GetString()!, out _)) throw new DispatchException("INVALID_ARGUMENT", "缺少必填参数。");
    var names = new HashSet<string>();
    foreach (var property in value.EnumerateObject())
    {
        if (!names.Add(property.Name) || !properties.TryGetProperty(property.Name, out var rule)) throw new DispatchException("INVALID_ARGUMENT", "未知或重复的参数。");
        var valid = rule.GetProperty("type").GetString() switch
        {
            "string" => property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()) && property.Value.GetString()!.Length <= rule.GetProperty("maxLength").GetInt32(),
            "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => property.Value.TryGetInt32(out var integer) && integer >= rule.GetProperty("minimum").GetInt32() && integer <= rule.GetProperty("maximum").GetInt32(),
            _ => false
        };
        if (!valid) throw new DispatchException("INVALID_ARGUMENT", "参数类型或范围无效。");
    }
}
