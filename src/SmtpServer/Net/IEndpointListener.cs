using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SmtpServer.IO;

namespace SmtpServer.Net
{
    /// <summary>
    /// Endpoint Listener Interface
    /// </summary>
    public interface IEndpointListener : IDisposable
    {
        /// <summary>
        /// Returns a securtable pipe to the endpoint.
        /// </summary>
        /// <param name="context">The session context that the pipe is being created for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The securable pipe from the endpoint.</returns>
        Task<ISecurableDuplexPipe> GetPipeAsync(ISessionContext context, CancellationToken cancellationToken);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tcpClient"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        event EventHandler<TcpClient> OnClientAccepted;
    }
}
