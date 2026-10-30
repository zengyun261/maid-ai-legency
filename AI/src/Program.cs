using AI;

// 确保 data/ 及子目录存在（图片、会话、记忆）
DataPaths.EnsureDirs();

var builder = WebApplication.CreateBuilder(args);

// 缩短关闭超时，避免长时间等待 SSE 连接
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(1);
});

// 注册核心依赖
builder.Services.AddSingleton<ChatEventSource>();
builder.Services.AddSingleton<MessageBus>();
builder.Services.AddSingleton<SleepState>();
builder.Services.AddSingleton<ImageStore>();
builder.Services.AddSingleton<MaidAgent>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<MaidService>();
builder.Services.AddSingleton(sp => new Lazy<MaidService>(sp.GetRequiredService<MaidService>));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MaidService>());
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(v => v.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());

// 注册 HTTP 端点（按职责分组，详见 EndpointExtensions）
app.MapChatEventStream();
app.MapNewMessage();
app.MapImageAccess();
app.MapLoopControl();
app.MapCompaction();
app.MapSession();

app.Run();
