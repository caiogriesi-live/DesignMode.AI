using DesignMode.AI.Shared.Interfaces.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DesignMode.AI.Common;
using Dapper.ColumnMapper;
using Dapper;

namespace DesignMode.AI.Data.Data
{
    public class TestDac : ITestDac
    {
        private readonly IConnectionManager connectionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PartDac"/> class.
        /// </summary>
        /// <param name="connectionManager">The connection manager.</param>
        public TestDac(IConnectionManager connectionManager)
        {
            this.connectionManager = connectionManager;
        }

        /// <summary>
        /// Gets or sets the database name.
        /// </summary>
        public string DatabaseName { get; set; }

        /// <summary>
        /// Gets the part.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>A Response.</returns>
        public Response<string> GetTest()
        {
            var response = new Response<string>();

            string testcolumn;

            using (IDbConnection connection = this.connectionManager.GetConnection())
            {
                string sql = @"
                            SELECT
                                top 1
                                testcolumn
                            FROM
                                testtable";

                using var results = connection.QueryMultiple(sql);

                testcolumn = results.Read<string>().FirstOrDefault();
            }

            response.Item = testcolumn;
            response.OperationSuccess = true;

            return response;
        }
    }
}
