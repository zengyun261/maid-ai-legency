using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;

namespace AI;

/// <summary>
/// HTTP 端点定义：按职责分组，保持 Program.cs 仅作启动入口
/// </summary>
public static class EndpointExtensions
{
    /// <summary>SSE 事件流</summary>
    public static WebApplication MapChatEventStream(this WebApplication app)
    {
        app.MapGet("/chat/events", async (HttpContext context, ChatEventSource eventSource, MaidService maid, MaidAgent maidAgent, CancellationToken ct) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";

            var clientId = eventSource.Subscribe();

            // 历史回放后，推送当前运行状态和模型上下文长度
            var statusJson = System.Text.Json.JsonSerializer.Serialize(new { running = maid.IsRunning });
            await context.Response.WriteAsync($"event: loop_status\ndata: {statusJson}\n\n", ct);
            var maxCtxJson = System.Text.Json.JsonSerializer.Serialize(new { maxTokens = maidAgent.MaxContextTokens });
            await context.Response.WriteAsync($"event: model_info\ndata: {maxCtxJson}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var sseMessage in eventSource.GetEventStream(clientId, ct))
                {
                    await context.Response.WriteAsync(sseMessage, ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            finally
            {
                eventSource.Unsubscribe(clientId);
            }
        });
        return app;
    }

    /// <summary>接收新消息（JSON body，支持图片）</summary>
    public static WebApplication MapNewMessage(this WebApplication app)
    {
        app.MapPost("/new_msg", async (HttpContext context, MessageBus messageBus, ImageStore imageStore) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            string text;
            string? imageId = null;
            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
                text = json.GetProperty("text").GetString() ?? "";
                if (json.TryGetProperty("image", out var imgProp))
                {
                    var base64 = imgProp.GetString();
                    if (base64 != null)
                    {
                        imageId = imageStore.Save(base64);
                    }
                }
            }
            else
            {
                text = body;
            }

            messageBus.Post(new UserMessage { Text = text, ImageId = imageId });
            return Results.Ok(new { imageId });
        });
        return app;
    }

    /// <summary>提供图片访问</summary>
    public static WebApplication MapImageAccess(this WebApplication app)
    {
        app.MapGet("/images/{id}", (string id, ImageStore imageStore) =>
        {
            var bytes = imageStore.Load(id);
            if (bytes == null) return Results.NotFound();
            return Results.File(bytes, "image/png");
        });
        return app;
    }

    /// <summary>Agent 循环控制：启动/停止/状态/跳过等待</summary>
    public static WebApplication MapLoopControl(this WebApplication app)
    {
        app.MapPost("/loop/start", async (MaidService maid) =>
        {
            await maid.StartLoopAsync();
            return Results.Ok(new { running = maid.IsRunning });
        });

        app.MapPost("/loop/stop", async (MaidService maid) =>
        {
            await maid.StopLoopAsync();
            return Results.Ok(new { running = maid.IsRunning });
        });

        app.MapGet("/loop/status", (MaidService maid) => Results.Ok(new { running = maid.IsRunning }));

        app.MapPost("/skip_wait", (MaidService maid) =>
        {
            maid.SafeWakeUp();
            return Results.Ok();
        });
        return app;
    }

    /// <summary>手动压缩与压缩并恢复</summary>
    public static WebApplication MapCompaction(this WebApplication app)
    {
        app.MapPost("/compact", [Experimental("MAAI001")] async (MaidService maid) =>
        {
            await maid.CompactAsync();
            return Results.Ok();
        });

        app.MapPost("/session/compact_and_restore", async (MaidService maid) =>
        {
            await maid.CompactAndRestoreAsync();
            return Results.Ok();
        });
        return app;
    }

    /// <summary>会话保存与恢复</summary>
    public static WebApplication MapSession(this WebApplication app)
    {
        app.MapPost("/session/save", async (MaidService maid) =>
        {
            await maid.SaveSessionAsync();
            return Results.Ok();
        });

        app.MapPost("/session/restore", async (MaidService maid) =>
        {
            await maid.RestoreSessionAsync();
            return Results.Ok();
        });

        // 列出历史快照
        app.MapGet("/sessions/history", async () =>
        {
            var snapshots = await SessionStore.ListSnapshotsAsync();
            return Results.Ok(snapshots);
        });

        // 恢复指定快照（覆盖主文件并启动循环）
        app.MapPost("/sessions/history/{name}/restore", async (string name, MaidService maid) =>
        {
            try
            {
                await SessionStore.RestoreSnapshotAsync(name);
                await maid.RestoreSessionAsync();
                return Results.Ok();
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        return app;
    }
}
