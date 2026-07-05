// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
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
		private readonly ConcurrentDictionary<int, StaThread> _workers;
		private int _disposed;

		private AtomicCounter _runningTaskCount = 0;
		private AtomicCounter _workerCount = 0;

		public static int MinimumWorkerCount { get; set; } = 8;
		public static int MaximumWorkerCount { get; set; } = 32;
		public static int WorkerIdleTimeoutSeconds { get; set; } = 30;

		/// <summary>
		/// Initializes a new pool with the specified number of STA worker threads.
		/// </summary>
		/// <param name="initialWorkerCount">Initial number of STA threads to create. Clamped to [<see cref="MinimumWorkerCount"/>, <see cref="MaximumWorkerCount"/>].</param>
		public STATaskPool(int initialWorkerCount)
		{
			initialWorkerCount = Math.Clamp(initialWorkerCount, MinimumWorkerCount, MaximumWorkerCount);

			_workQueue = new BlockingCollection<WorkItemBase>(
				new ConcurrentQueue<WorkItemBase>());

			_workers = new();
			for (int i = 0; i < initialWorkerCount; i++)
			{
				CreateWorkerThread();
			}
		}

		private StaThread CreateWorkerThread()
		{
			int id = Random.Shared.Next();
			var thread = new Thread(WorkerLoop)
			{
				IsBackground = true,
				Name = $"STA Worker"
			};
			var threadObj = new StaThread(id, thread);
			_workers[id] = threadObj;
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start(threadObj);
			_workerCount++;

			return threadObj;
		}

		private bool ShouldCreateNewWorker()
		{
			int runningTasks = _workQueue.Count;
			int workerCount = _workerCount.Value;
			// Create a new worker if all existing workers are busy and we haven't reached the maximum limit.
			return runningTasks >= workerCount && workerCount < MaximumWorkerCount;
		}

		private void SpawnNewWorkerIfNeeded()
		{
			lock (this)
			{
				if (ShouldCreateNewWorker())
				{
					CreateWorkerThread();
				}
			}
		}

		private bool TryRegisterWorkerExit(StaThread staThread)
		{
			lock (this)
			{
				if (_workerCount.Value > MinimumWorkerCount)
				{
					_workerCount--;
					_workers.TryRemove(staThread.Id, out _);
					return true;
				}
				return false;
			}
		}

		public void WaitForRunningTasksToComplete()
		{
			while (_runningTaskCount.Value > 0)
			{
				Thread.Sleep(10);
			}
		}

		/// <summary>
		/// Enqueues a synchronous <see cref="Action"/> for execution on an STA thread.
		/// </summary>
		public Task Enqueue(Action<CancellationToken> action, ILogger? logger, CancellationToken token)
		{
			ThrowIfDisposed();
			var item = new SyncActionWorkItem(action, logger, token);
			_workQueue.Add(item);
			SpawnNewWorkerIfNeeded();
			return item.Task;
		}

		/// <summary>
		/// Enqueues a synchronous <see cref="Func{T}"/> for execution on an STA thread.
		/// </summary>
		public Task<T> Enqueue<T>(Func<CancellationToken, T> func, ILogger? logger, CancellationToken token)
		{
			ThrowIfDisposed();
			var item = new SyncFuncWorkItem<T>(func, logger, token);
			_workQueue.Add(item);
			SpawnNewWorkerIfNeeded();
			return item.Task;
		}

		/// <summary>
		/// Enqueues an async delegate (<see cref="Func{Task}"/>) for execution on an STA thread.
		/// The STA thread is freed after the synchronous portion (up to the first await) completes.
		/// </summary>
		public Task EnqueueAsync(Func<CancellationToken, Task> func, ILogger? logger, CancellationToken token)
		{
			ThrowIfDisposed();
			var item = new AsyncActionWorkItem(func, logger, token);
			_workQueue.Add(item);
			SpawnNewWorkerIfNeeded();
			return item.Task;
		}

		/// <summary>
		/// Enqueues an async delegate (<see cref="Func{Task{T}}"/>) for execution on an STA thread.
		/// The STA thread is freed after the synchronous portion (up to the first await) completes.
		/// </summary>
		public Task<T?> EnqueueAsync<T>(Func<CancellationToken, Task<T>> func, ILogger? logger, CancellationToken token)
		{
			ThrowIfDisposed();
			var item = new AsyncFuncWorkItem<T>(func, logger, token);
			_workQueue.Add(item);
			SpawnNewWorkerIfNeeded();
			return item.Task;
		}

		/// <summary>
		/// The main loop executed by each STA worker thread.
		/// </summary>
		private void WorkerLoop(object? arg)
		{
			if(arg is not StaThread staThread)
				throw new ArgumentException("WorkerLoop must be called with a StaThread argument.", nameof(arg));
			WorkItemBase? workItem;
			int timeoutMs = (int)TimeSpan.FromSeconds(WorkerIdleTimeoutSeconds).TotalMilliseconds;
			PInvoke.OleInitialize();
			try
			{
				while (true)
				{
					if (_workQueue.TryTake(out workItem, timeoutMs))
					{
						try
						{
							staThread.Status = StaThreadStatus.Running;
							_runningTaskCount++;

							workItem.Execute();

							staThread.LastActivity = DateTimeOffset.UtcNow;
						}
						finally
						{
							_runningTaskCount--;
							staThread.Status = StaThreadStatus.Idle;
						}
					}
					else
					{
						if (TryRegisterWorkerExit(staThread))
						{
							return;
						}
					}
				}
			}
			finally
			{
				PInvoke.OleUninitialize();
				staThread.Status = StaThreadStatus.Disposed;
				_workerCount--;
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

			foreach (var worker in _workers.Values)
			{
				// Wait up to 3 seconds per thread to avoid hanging on shutdown
				// if a thread is stuck in a long shell operation.
				worker.Thread.Join(TimeSpan.FromSeconds(3));
			}

			_workQueue.Dispose();
		}

		private class StaThread
		{
			public int Id { get; }
			public Thread Thread { get; }
			public StaThreadStatus Status { get; set; }
			public DateTimeOffset LastActivity { get; set; }

			public StaThread(int id, Thread thread)
			{
				Id = id;
				Thread = thread;
				Status = StaThreadStatus.Idle;
				LastActivity = DateTimeOffset.UtcNow;
			}
		}

		private enum StaThreadStatus
		{
			Idle,
			Running,
			Disposed
		}
	}

	public class AtomicCounter
	{
		private int _value;
		public AtomicCounter(int initialValue = 0)
		{
			_value = initialValue;
		}
		public int Increment()
		{
			return Interlocked.Increment(ref _value);
		}
		public int Decrement()
		{
			return Interlocked.Decrement(ref _value);
		}
		public int Value => Volatile.Read(ref _value);

		public static implicit operator int(AtomicCounter counter) => counter.Value;
		public static implicit operator AtomicCounter(int value) => new AtomicCounter(value);

		public static AtomicCounter operator++(AtomicCounter counter)
		{
			counter.Increment();
			return counter;
		}

		public static AtomicCounter operator --(AtomicCounter counter)
		{
			counter.Decrement();
			return counter;
		}
	}
}
