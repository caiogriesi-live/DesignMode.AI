using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using System.Data.SqlClient;
using System.Data;
using System.Linq;
using System;
using DesignMode.AI.Shared.Models.Settings;
using DesignMode.AI.Shared.Models;

namespace DesignMode.AI.Data
{

    /// <summary>
    /// The connection manager.
    /// </summary>
    public class ConnectionManager : IConnectionManager
    {
        private ConectionStringSettings connectionStrings;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionManager"/> class.
        /// </summary>
        /// <param name="connectionStringsSettings">The connection strings settings.</param>
        public ConnectionManager(IOptions<ConectionStringSettings> connectionStringsSettings)
        {
            this.connectionStrings = connectionStringsSettings.Value;
        }

        /// <summary>
        /// Gets the connection.
        /// </summary>
        /// <param name="connectionName">The connection name.</param>
        /// <returns>An IDbConnection.</returns>
        public IDbConnection GetConnection()
        {
            var connStr = this.connectionStrings.Value;

            IDbConnection connection = new SqlConnection(connStr);

            connection.Open();
            return connection;
        }
    }
}
