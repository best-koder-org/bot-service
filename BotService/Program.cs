using BotService.Configuration;
using BotService.Data;
using BotService.Services;
using BotService.Services.BotModes;
using BotService.Services.Content;
using BotService.Services.Conversation;
using BotService.Services.Keycloak;
using BotService.Services.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "BotService")
    .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/bot-service-.log", rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Configuration
builder.Services.Configure<BotServiceOptions>(builder.Configuration.GetSection("BotService"));

// Database
builder.Services.AddDbContext<BotDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("BotDb") ?? "Data Source=bot-service.db"));

// HTTP clients
builder.Services.AddHttpClient<KeycloakBotProvisioner>();
builder.Services.AddHttpClient<DatingAppApiClient>();
builder.Services.AddHttpClient<GeminiLlmProvider>();
builder.Services.AddHttpClient<GroqLlmProvider>();
builder.Services.AddHttpClient<OllamaLlmProvider>();
builder.Services.AddHttpClient(); // IHttpClientFactory for ChaosAgent

// Services
builder.Services.AddSingleton<BotPersonaEngine>(sp =>
{
    var engine = new BotPersonaEngine(sp.GetRequiredService<ILogger<BotPersonaEngine>>());
    var personasDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Personas");
    engine.LoadPersonas(personasDir);
    return engine;
});

builder.Services.AddSingleton<MessageContentProvider>(sp =>
{
    var provider = new MessageContentProvider(sp.GetRequiredService<ILogger<MessageContentProvider>>());
    var contentDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content");
    provider.LoadMessages(contentDir);
    return provider;
});

builder.Services.AddScoped<KeycloakBotProvisioner>();
builder.Services.AddScoped<DatingAppApiClient>();

// ── LLM providers (Wave 0) ──
builder.Services.AddSingleton<GeminiLlmProvider>();
builder.Services.AddSingleton<GroqLlmProvider>();
builder.Services.AddSingleton<OllamaLlmProvider>();
builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<GeminiLlmProvider>());
builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<GroqLlmProvider>());
builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<OllamaLlmProvider>());
builder.Services.AddSingleton<LlmRouter>();

// ── Conversation engines (Wave 1) ──
builder.Services.AddSingleton<CannedConversationEngine>();
builder.Services.AddSingleton<LlmConversationEngine>();
builder.Services.AddSingleton<HybridConversationEngine>();

// Register IConversationEngine based on config (hybrid/llm/canned)
builder.Services.AddSingleton<IConversationEngine>(sp =>
{
    var config = sp.GetRequiredService<IOptions<BotServiceOptions>>().Value;
    return config.Conversation.Engine.ToLowerInvariant() switch
    {
        "llm" => sp.GetRequiredService<LlmConversationEngine>(),
        "canned" => sp.GetRequiredService<CannedConversationEngine>(),
        _ => sp.GetRequiredService<HybridConversationEngine>() // "hybrid" or default
    };
});

// ── Bot Observer (Wave 2) ──
builder.Services.AddSingleton<BotService.Services.Observer.BotObserver>();

// ── Swarm Orchestrator (Wave 3) ──
builder.Services.AddSingleton<BotService.Services.Swarm.SwarmOrchestrator>();

// Bot mode background services
builder.Services.AddHostedService<SyntheticUserService>();
builder.Services.AddHostedService<WarmupBotService>();
builder.Services.AddHostedService<LoadActorService>();
builder.Services.AddHostedService<ChaosAgentService>();
builder.Services.AddHostedService<BotService.Services.Observer.BotReporter>();

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Bot Service API", Version = "v1" });
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath);
});

// Health check
builder.Services.AddHealthChecks()
    .AddDbContextCheck<BotDbContext>("sqlite");

var app = builder.Build();

// Ensure DB created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Middleware
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Ok(new
{
    service = "bot-service",
    version = "2.0.0",
    status = "running",
    features = new[] { "llm-conversations", "hybrid-engine", "multi-provider" },
    docs = "/swagger"
}));

Log.Information("Bot Service starting on port {Port}", builder.Configuration["Urls"] ?? "http://localhost:8089");

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Bot Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
