// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Threading;

namespace Files.App.Storage
{
	/// <summary>
	/// Base class for work items dispatched to the STA thread pool.
	/// </summary>
	internal abstract class WorkItemBase
	{
		/// <summary>
		/// Executes the work item on the STA thread.
		/// </summary>
		public abstract void Execute();

		/// <summary>
		/// Logs an exception that occurred during execution.
		/// Preserves the existing behavior: exceptions are swallowed and only logged.
		/// </summary>
		protected static void LogException(Exception ex, ILogger? logger)
		{
			logger?.LogWarning(ex, "An exception was occurred during the execution within STA.");
		}
	}

	/// <summary>
	/// Work item that executes a synchronous <see cref="Action"/> on an STA thread.
	/// </summary>
	internal sealed class SyncActionWorkItem : WorkItemBase
	{
		private readonly Action _action;
		private readonly ILogger? _logger;
		private readonly TaskCompletionSource _tcs;

		public SyncActionWorkItem(Action action, ILogger? logger)
		{
			_action = action;
			_logger = logger;
			_tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public Task Task => _tcs.Task;

		public override void Execute()
		{
			try
			{
				_action();
				_tcs.SetResult();
			}
			catch (Exception ex)
			{
				LogException(ex, _logger);
				_tcs.SetResult();
			}
		}
	}

	/// <summary>
	/// Work item that executes a synchronous <see cref="Func{T}"/> on an STA thread.
	/// </summary>
	internal sealed class SyncFuncWorkItem<T> : WorkItemBase
	{
		private readonly Func<T> _func;
		private readonly ILogger? _logger;
		private readonly TaskCompletionSource<T> _tcs;

		public SyncFuncWorkItem(Func<T> func, ILogger? logger)
		{
			_func = func;
			_logger = logger;
			_tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public Task<T> Task => _tcs.Task;

		public override void Execute()
		{
			try
			{
				_tcs.SetResult(_func());
			}
			catch (Exception ex)
			{
				LogException(ex, _logger);
				_tcs.SetResult(default!);
			}
		}
	}

	/// <summary>
	/// Work item that executes an async delegate (<see cref="Func{Task}"/>) on an STA thread.
	/// The delegate runs with a <see cref="StaSynchronizationContext"/> installed and the worker pumps
	/// it until the returned task completes, so continuations after an await stay on the same STA thread.
	/// </summary>
	internal sealed class AsyncActionWorkItem : WorkItemBase
	{
		private readonly Func<Task> _func;
		private readonly ILogger? _logger;
		private readonly TaskCompletionSource _tcs;

		public AsyncActionWorkItem(Func<Task> func, ILogger? logger)
		{
			_func = func;
			_logger = logger;
			_tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public Task Task => _tcs.Task;

		public override void Execute()
		{
			var previousContext = SynchronizationContext.Current;
			using var syncContext = new StaSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(syncContext);

			try
			{
				var innerTask = _func();

				// Pump posted continuations instead of freeing the worker, so post-await code
				// keeps running on this STA thread.
				if (!innerTask.IsCompleted)
					syncContext.Run(innerTask);

				CompleteFromCompletedTask(innerTask);
			}
			catch (Exception ex)
			{
				// Exception thrown synchronously before the first await
				LogException(ex, _logger);
				_tcs.SetResult();
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext(previousContext);
			}
		}

		private void CompleteFromCompletedTask(Task completedTask)
		{
			if (completedTask.IsFaulted)
			{
				LogException(
					completedTask.Exception?.InnerException ?? completedTask.Exception!,
					_logger);
				_tcs.SetResult();
			}
			else if (completedTask.IsCanceled)
			{
				_tcs.SetResult();
			}
			else
			{
				_tcs.SetResult();
			}
		}
	}

	/// <summary>
	/// Work item that executes an async delegate (<see cref="Func{Task{T}}"/>) on an STA thread.
	/// Same pumping pattern as <see cref="AsyncActionWorkItem"/>: the worker keeps the
	/// <see cref="StaSynchronizationContext"/> installed until the returned task completes.
	/// </summary>
	internal sealed class AsyncFuncWorkItem<T> : WorkItemBase
	{
		private readonly Func<Task<T>> _func;
		private readonly ILogger? _logger;
		private readonly TaskCompletionSource<T?> _tcs;

		public AsyncFuncWorkItem(Func<Task<T>> func, ILogger? logger)
		{
			_func = func;
			_logger = logger;
			_tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public Task<T?> Task => _tcs.Task;

		public override void Execute()
		{
			var previousContext = SynchronizationContext.Current;
			using var syncContext = new StaSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(syncContext);

			try
			{
				var innerTask = _func();

				// Pump posted continuations instead of freeing the worker, so post-await code
				// keeps running on this STA thread.
				if (!innerTask.IsCompleted)
					syncContext.Run(innerTask);

				CompleteFromCompletedTask(innerTask);
			}
			catch (Exception ex)
			{
				// Exception thrown synchronously before the first await
				LogException(ex, _logger);
				_tcs.SetResult(default);
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext(previousContext);
			}
		}

		private void CompleteFromCompletedTask(Task<T> completedTask)
		{
			if (completedTask.IsFaulted)
			{
				LogException(
					completedTask.Exception?.InnerException ?? completedTask.Exception!,
					_logger);
				_tcs.SetResult(default);
			}
			else if (completedTask.IsCanceled)
			{
				_tcs.SetResult(default);
			}
			else
			{
				_tcs.SetResult(completedTask.Result);
			}
		}
	}
}
