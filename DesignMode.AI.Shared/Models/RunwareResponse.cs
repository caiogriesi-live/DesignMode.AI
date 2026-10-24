using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesignMode.AI.Shared.Models
{
    using System.Collections.Generic;

    public class RunwareResponse
    {
        public List<RunwareImageData> Data { get; set; }
    }

    public class RunwareImageData
    {
        public string TaskType { get; set; }
        public string ImageUUID { get; set; }
        public string TaskUUID { get; set; }
        public double Cost { get; set; }
        public long Seed { get; set; }
        public string ImageURL { get; set; }
        public string ImageDataURI { get; set; }
    }
}
