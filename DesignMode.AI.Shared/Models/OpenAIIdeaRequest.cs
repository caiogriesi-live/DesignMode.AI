using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesignMode.AI.Shared.Models
{
    public class OpenAIIdeaRequest
    {
        public string Name { get; set; }
        public string Industry { get; set; }
        public string Prompt { get; set; }
        public string CompanyDescription { get; set; } = string.Empty;
        public List<ImageIdeaItem> Items { get; set; }
        public string? GenerationJobId { get; set; }
        public string? ProjectId { get; set; }
        public string? FunnelId { get; set; }
    }

    public class ImageIdeaItem
    {
        public string Id { get; set; } = string.Empty;       // NEW
        public string CountId { get; set; } = string.Empty;  // NEW
        public string DeliverableType { get; set; } = string.Empty;
        public string SubCategory { get; set; } = string.Empty;
        public string ExtractedText { get; set; } = string.Empty;
        public string RawPrompt { get; set; } = string.Empty;
    }
}
