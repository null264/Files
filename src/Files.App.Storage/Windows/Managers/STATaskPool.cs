// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Windows.Win32;

namespace Files.App.Storage
{
	/// <summary>
	/// A bounded pool of long-lived STA threads that process work items from a shared queue.
	/// Replaces the per-call <see cref="Thread"/> creation in <see cref="STATask.Run(Action, ILogger?)"/>
	/// and its overloads.
	/// </summary>
	internal sealed class STATaskPool : IDisposable
	{
		private readonly BlockingCollection<WorkItemBase> _workQueue;
		private readonly Thread[] _workers;
		private int _disposed;

		/// <summary>
		/// Initializes a new pool with the specified number of STA worker threads.
		/// </summary>
		/// <param name="workerCount">Number of STA threads to create. Clamped to [2, 8].</param>
		public STATaskPool(int workerCount)
		{
			workerCount = Math.Clamp(workerCount, 2, 8);

			_workQueue = new BlockingCollection<WorkItemBase>(
				new ConcurrentQueue<WorkItemBase>());

			_workers = new Thread[workerCount];
			for (int i = 0; i < workerCount; i++)
			{
				_workers[i] = new Thread(WorkerLoop)
				{
					IsBackground = true,
					Name = $"STATask-Worker-{i}"
				};
				_workers[i].SetApartmentState(ApartmentState.STA);
				_workers[i].Start();
			}
		}

		/// <summary>
		/// Enqueues a synchronous <see cref="Action"/> for execution on an STA thread.
		/// </summary>
		public Task Enqueue(Action action, ILogger? logger)
		{
			ThrowIfDisposed();
			var item = new SyncActionWorkItem(action, logger);
			_workQueue.Add(item);
			return item.Task;
		}

		/// <summary>
		/// Enqueues a synchronous <see cref="Func{T}"/> for execution on an STA thread.
		/// </summary>
		public Task<T> Enqueue<T>(Func<T> func, ILogger? logger)
		{
			ThrowIfDisposed();
			var item = new SyncFuncWorkItem<T>(func, logger);
			_workQueue.Add(item);
			return item.Task;
		}

		/// <summary>
		/// Enqueues an async delegate (<see cref="Func{Task}"/>) for execution on an STA thread.
		/// The STA thread is freed after the synchronous portion (up to the first await) completes.
		/// </summary>
		public Task EnqueueAsync(Func<Task> func, ILogger? logger)
		{
			ThrowIfDisposed();
			var item = new AsyncActionWorkItem(func, logger);
			_workQueue.Add(item);
			return item.Task;
		}

		/// <summary>
		/// Enqueues an async delegate (<see cref="Func{Task{T}}"/>) for execution on an STA thread.
		/// The STA thread is freed after the synchronous portion (up to the first await) completes.
		/// </summary>
		public Task<T?> EnqueueAsync<T>(Func<Task<T>> func, ILogger? logger)
		{
			ThrowIfDisposed();
			var item = new AsyncFuncWorkItem<T>(func, logger);
			_workQueue.Add(item);
			return item.Task;
		}

		/// <summary>
		/// The main loop executed by each STA worker thread.
		/// </summary>
		private void WorkerLoop()
		{
			PInvoke.OleInitialize();
			try
			{
				foreach (var workItem in _workQueue.GetConsumingEnumerable())
				{
					workItem.Execute();
				}
			}
			finally
			{
				PInvoke.OleUninitialize();
			}
		}

		private void ThrowIfDisposed()
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		}

		/// <summary>
		/// Signals all workers to complete and waits for them to finish.
		/// </summary>
		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;

			_workQueue.CompleteAdding();

			foreach (var worker in _workers)
			{
				// Wait up to 3 seconds per thread to avoid hanging on shutdown
				// if a thread is stuck in a long shell operation.
				worker.Join(TimeSpan.FromSeconds(3));
			}

			_workQueue.Dispose();
		}
	}
}
