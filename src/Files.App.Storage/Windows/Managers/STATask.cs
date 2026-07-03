// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

namespace Files.App.Storage
{
	/// <summary>
	/// Represents a work scheduled to execute on a STA thread.
	/// Uses a bounded thread pool to avoid the overhead of per-call thread creation.
	/// </summary>
	public partial class STATask
	{
		private static int GetWorkerCount()
			=> Math.Clamp(Environment.ProcessorCount, 2, 8);

		private static readonly Lazy<STATaskPool> _pool =
			new(() => new STATaskPool(GetWorkerCount()),
				LazyThreadSafetyMode.ExecutionAndPublication);

		public static void WaitForWorkers() => _pool.Value.WaitForRunningTasksToComplete();

		/// <summary>
		/// Schedules the specified work to execute on a pooled STA thread.
		/// </summary>
		/// <param name="action">The work to execute in the STA thread.</param>
		/// <param name="logger">A logger to capture any exception that occurs during execution.</param>
		/// <returns>A <see cref="Task"/> that represents the work scheduled to execute in the STA thread.</returns>
		public static Task Run(Action action, ILogger? logger, CancellationToken token)
			=> _pool.Value.Enqueue(action, logger, token);

		/// <summary>
		/// Schedules the specified work to execute on a pooled STA thread.
		/// </summary>
		/// <typeparam name="T">The type of the result returned by the function.</typeparam>
		/// <param name="func">The work to execute in the STA thread.</param>
		/// <param name="logger">A logger to capture any exception that occurs during execution.</param>
		/// <returns>A <see cref="Task"/> that represents the work scheduled to execute in the STA thread.</returns>
		public static Task<T> Run<T>(Func<T> func, ILogger? logger, CancellationToken token)
			=> _pool.Value.Enqueue(func, logger, token);

		/// <summary>
		/// Schedules the specified work to execute on a pooled STA thread.
		/// </summary>
		/// <param name="func">The work to execute in the STA thread.</param>
		/// <param name="logger">A logger to capture any exception that occurs during execution.</param>
		/// <returns>A <see cref="Task"/> that represents the work scheduled to execute in the STA thread.</returns>
		public static Task Run(Func<Task> func, ILogger? logger, CancellationToken token)
			=> _pool.Value.EnqueueAsync(func, logger, token);

		/// <summary>
		/// Schedules the specified work to execute on a pooled STA thread.
		/// </summary>
		/// <typeparam name="T">The type of the result returned by the function.</typeparam>
		/// <param name="func">The work to execute in the STA thread.</param>
		/// <param name="logger">A logger to capture any exception that occurs during execution.</param>
		/// <returns>A <see cref="Task"/> that represents the work scheduled to execute in the STA thread.</returns>
		public static Task<T?> Run<T>(Func<Task<T>> func, ILogger? logger, CancellationToken token)
			=> _pool.Value.EnqueueAsync(func, logger, token);
	}
}
