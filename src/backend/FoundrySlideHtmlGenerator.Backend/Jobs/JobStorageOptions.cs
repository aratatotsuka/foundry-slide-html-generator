using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public sealed class JobStorageOptions
{
    [ConfigurationKeyName("JOB_DATA_DIR")]
    [Required]
    public string JobDataDir { get; init; } = "data/jobs";
}

