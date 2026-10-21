// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Files.App.Storage
{
	/// <summary>
	/// A pumpable <see cref="SynchronizationContext"/> bound to the STA thread that created it.
	/// While a work item runs, continuations posted after an await stay on that same thread, so
	/// STA-affine COM objects created before the await stay usable after it. Ported from upstream's
	/// per-thread <c>STATask</c> so the pooled scheduler keeps the same apartment guarantees.
	/// </summary>
	internal sealed class StaSynchronizationContext : SynchronizationContext, IDisposable
	{
		private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
		private readonly Lock _queueLock = new();
		private readonly int _threadId = Environment.CurrentManagedThreadId;
		private bool _acceptingWork = true;

		public override void Post(SendOrPostCallback callback, object? state)
			=> TryPost(callback, state);

		public override void Send(SendOrPostCallback callback, object? state)
		{
			if (Environment.CurrentManagedThreadId == _threadId)
			{
				callback(state);
				return;
			}

			Exception? exception = null;
			using var completed = new ManualResetEventSlim();
			if (!TryPost(
				_ =>
				{
					try
					{
						callback(state);
					}
					catch (Exception ex)
					{
						exception = ex;
					}
					finally
					{
						completed.Set();
					}
				},
				null))
			{
				throw new InvalidOperationException("The STA synchronization context has completed.");
			}
			completed.Wait();

			if (exception is not null)
				ExceptionDispatchInfo.Capture(exception).Throw();
		}

		/// <summary>
		/// Pumps posted callbacks on the current thread until <paramref name="task"/> completes.
		/// </summary>
		public void Run(Task task)
		{
			var completionSignal = task.ContinueWith(
				_ => Complete(),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);

			foreach (var work in _queue.GetConsumingEnumerable())
				work.Callback(work.State);

			completionSignal.GetAwaiter().GetResult();
		}

		private bool TryPost(SendOrPostCallback callback, object? state)
		{
			lock (_queueLock)
			{
				if (!_acceptingWork)
					return false;

				_queue.Add((callback, state));
				return true;
			}
		}

		private void Complete()
		{
			lock (_queueLock)
			{
				if (!_acceptingWork)
					return;

				_acceptingWork = false;
				_queue.CompleteAdding();
			}
		}

		public void Dispose()
		{
			Complete();
			_queue.Dispose();
		}
	}
}
