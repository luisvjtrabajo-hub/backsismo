namespace SismoAI.Domain;

public sealed class EarthquakeEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public DateTimeOffset OriginTimeUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double DepthKm { get; set; }
    public double Magnitude { get; set; }
    public string MagnitudeType { get; set; } = string.Empty;
    public string LocationDescription { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RawPayload { get; set; } = string.Empty;
    public double ApproximateEnergyJoules { get; set; }
    public double AnomalyScore { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AnomalySnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public double Score { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string DriversJson { get; set; } = "[]";
}

public sealed class SourceSyncState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceName { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DateTimeOffset? LastIngestedEventUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
