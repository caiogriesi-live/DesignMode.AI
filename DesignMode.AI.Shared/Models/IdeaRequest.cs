using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesignMode.AI.Shared.Models
{
    public class IdeaRequest
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string UserId { get; set; } // Add this if not already present
        public string CompanyIdeaId { get; set; } // Add this if not already present
    }
}
