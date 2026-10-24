using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;

namespace DesignMode.AI.Data
{
    /// <summary>
    /// The connection manager.
    /// </summary>
    public interface IConnectionManager
    {
        /// <summary>
        /// Gets the connection.
        /// </summary>
        /// <param name="connectionName">The connection name.</param>
        /// <returns>An IDbConnection.</returns>
        public IDbConnection GetConnection();
    }
}
