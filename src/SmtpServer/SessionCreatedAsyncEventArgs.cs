using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SmtpServer
{
    public class SessionCreatedAsyncEventArgs
    {
        public SessionEventArgs OriginalArgs { get; }
        private readonly List<Task> _deferrals = new List<Task>();

        public SessionCreatedAsyncEventArgs(SessionEventArgs args) => OriginalArgs = args;

        // Подписчик вызывает этот метод, чтобы зарегистрировать свою асинхронную задачу
        public void RegisterDeferral(Task task) => _deferrals.Add(task);

        // Метод для внутреннего использования, который возвращает общую задачу
        internal Task WaitForDeferralsAsync() => Task.WhenAll(_deferrals);
    }
}
