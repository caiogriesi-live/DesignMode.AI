namespace DesignMode.AI.Shared.Models.Attribution;

public sealed class AcquisitionCaptureRequest
{
    public string VisitorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? FunnelId { get; set; }
    public string? Gclid { get; set; }
    public string? Gbraid { get; set; }
    public string? Wbraid { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmTerm { get; set; }
    public string? UtmContent { get; set; }
    public string? CampaignId { get; set; }
    public string? AdGroupId { get; set; }
    public string? AdId { get; set; }
    public string? Keyword { get; set; }
    public string? MatchType { get; set; }
    public string? Device { get; set; }
    public string? Network { get; set; }
    public string? LandingPage { get; set; }
    public string? LandingVariant { get; set; }
    public string? Referrer { get; set; }
}

public sealed class AcquisitionCaptureResponse
{
    public string VisitorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string FunnelId { get; set; } = string.Empty;
    public string AcquisitionId { get; set; } = string.Empty;
}

public sealed class CreateSocialProjectRequest
{
    public string VisitorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string FunnelId { get; set; } = string.Empty;
    public string? LegacyCompanyIndustryId { get; set; }
    public string? LegacyCompanyIdeaId { get; set; }
    public string? LegacyInviteId { get; set; }
}

public sealed class ProjectContextResponse
{
    public string ProjectId { get; set; } = string.Empty;
    public string FunnelId { get; set; } = string.Empty;
    public string? AcquisitionId { get; set; }
    public string? CompanyIndustryId { get; set; }
}

public sealed class BindIdentityRequest
{
    public string VisitorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string FunnelId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string? LegacyCompanyIndustryId { get; set; }
    public string? LegacyCompanyIdeaId { get; set; }
    public string? LegacyInviteId { get; set; }
}

public sealed class CheckoutAttemptContext
{
    public string? ProjectId { get; set; }
    public string? FunnelId { get; set; }
}

public sealed class AdditionalProjectRequest
{
    public string? Name { get; set; }
    public string? Industry { get; set; }
    public string? FunnelId { get; set; }
    public string? LegacyCompanyIndustryId { get; set; }
    public string? LegacyCompanyIdeaId { get; set; }
    public string? LegacyInviteId { get; set; }
}

public sealed class ProjectSummary
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = "Untitled project";
    public string? Industry { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Status { get; set; } = "In progress";
    public string FunnelId { get; set; } = string.Empty;
    public string? CompanyIndustryId { get; set; }
}

public sealed class LegacyProjectResolutionRequest
{
    public string? LegacyCompanyIndustryId { get; set; }
    public string? LegacyCompanyIdeaId { get; set; }
    public string? LegacyInviteId { get; set; }
}
