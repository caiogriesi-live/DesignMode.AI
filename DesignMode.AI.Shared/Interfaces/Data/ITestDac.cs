using DesignMode.AI.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesignMode.AI.Shared.Interfaces.Data
{
    public interface ITestDac
    {
        /// <summary>
        /// Gets or sets the database name.
        /// </summary>
        public string DatabaseName { get; set; }

        /// <summary>
        /// Gets the part.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>A Response.</returns>
        public Response<string> GetTest();
    }
}
