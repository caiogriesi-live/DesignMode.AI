using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesignMode.AI.Shared.Models
{
    public class CompanyIndustryRequest
    {
        public string Name { get; set; }
        public string Industry { get; set; }
        public string UserId { get; set; }
        public string? ProjectId { get; set; }
    }
}
