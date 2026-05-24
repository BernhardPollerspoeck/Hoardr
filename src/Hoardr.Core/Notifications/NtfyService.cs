using System.Text;
using Hoardr.Core.Data;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Notifications;

/// <summary>Mutable so the Blazor admin form can two-way bind to it.</summary>
public sealed class NtfyConfig
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public string Token { get; set; } = "";
    public int ThresholdPercent { get; set; } = 80;

    public static NtfyConfig Default => new();
}

/// <summary>Reads/writes the ntfy config and publishes notifications to an ntfy topic.</summary>
public sealed class NtfyService(ISproutDatabase db, HttpClient http)
{
    public NtfyConfig GetConfig()
    {
        var data = db.Exec("get ntfy_config").Data;
        if (data is not { Count: > 0 })
            return NtfyConfig.Default;

        var row = data[0];
        return new NtfyConfig
        {
            Enabled = row.Bool("enabled"),
            Server = row.Str("server"),
            Topic = row.Str("topic"),
            Token = row.Str("token"),
            ThresholdPercent = (int)row.I64("threshold_percent"),
        };
    }

    public void SaveConfig(NtfyConfig config)
    {
        var data = db.Exec("get ntfy_config").Data;
        var body =
            $"enabled: {(config.Enabled ? "true" : "false")}, server: {Q(config.Server)}, topic: {Q(config.Topic)}, token: {Q(config.Token)}, threshold_percent: {config.ThresholdPercent}";

        if (data is { Count: > 0 })
            db.Exec($"upsert ntfy_config {{_id: {data[0].U64("_id")}, {body}}}");
        else
            db.Exec($"upsert ntfy_config {{{body}}}");
    }

    /// <summary>Publishes a notification. Returns false if disabled, misconfigured, or the POST fails.</summary>
    public async Task<bool> SendAsync(string title, string message, string priority = "default", string? tags = null, CancellationToken ct = default)
    {
        var config = GetConfig();
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.Topic) || string.IsNullOrWhiteSpace(config.Server))
            return false;

        try
        {
            var url = $"{config.Server.TrimEnd('/')}/{config.Topic}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(message, Encoding.UTF8),
            };
            request.Headers.TryAddWithoutValidation("Title", title);
            request.Headers.TryAddWithoutValidation("Priority", priority);
            if (!string.IsNullOrWhiteSpace(tags))
                request.Headers.TryAddWithoutValidation("Tags", tags);
            if (!string.IsNullOrWhiteSpace(config.Token))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.Token}");

            var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
