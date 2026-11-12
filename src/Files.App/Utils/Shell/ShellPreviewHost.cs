// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Files.App.Utils.Shell
{
	/// <summary>
	/// Runs shell preview handler work on a dedicated STA thread that pumps messages.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>IPreviewHandler</c> is synchronous by contract, so the only way to keep the UI thread
	/// responsive is to run the handler on a thread we control. That thread must be an STA and it must
	/// pump: handlers call back into our apartment through <c>IPreviewHandlerFrame</c>, and a handler
	/// hosted in-process creates its child windows on the calling thread.
	/// </para>
	/// <para>
	/// Work items run in FIFO order, which is what keeps teardown ordered after creation. Unlike
	/// <see cref="ThreadWithMessageQueue"/> a throwing item is reported to its caller and the loop
	/// keeps running.
	/// </para>
	/// </remarks>
	public sealed partial class ShellPreviewHost : Disposable
	{
		// A short wait cap keeps Dispose responsive without busy looping.
		const uint WaitTimeoutMilliseconds = 100;

		readonly ConcurrentQueue<WorkItem> workItems = new();

		// A kernel handle, so the wait can dispatch COM callbacks and window messages while it waits.
		readonly ManualResetEvent wakeEvent = new(false);

		readonly Thread thread;

		readonly int threadId;

		volatile bool stopping;

		public ShellPreviewHost(string name)
		{
			thread = new Thread(ThreadLoop)
			{
				IsBackground = true,
				Name = name,
			};
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();

			threadId = thread.ManagedThreadId;
		}

		/// <summary>
		/// Whether the calling thread is the host thread. Handler calls belong on that thread only.
		/// </summary>
		public bool IsCurrentThread => Environment.CurrentManagedThreadId == threadId;

		/// <summary>
		/// Queues <paramref name="work"/> for the host thread.
		/// </summary>
		/// <returns>A task that completes when the work ran, or faults with the exception it threw.</returns>
		public Task Post(Action work)
		{
			ArgumentNullException.ThrowIfNull(work);

			var item = new ActionWorkItem(work);
			Enqueue(item);

			return item.Task;
		}

		/// <summary>
		/// Queues <paramref name="work"/> for the host thread and completes with its result.
		/// </summary>
		public Task<T> Post<T>(Func<T> work)
		{
			ArgumentNullException.ThrowIfNull(work);

			var item = new FuncWorkItem<T>(work);
			Enqueue(item);

			return item.Result;
		}

		protected override void Dispose(bool disposing)
		{
			if (!disposing || stopping)
				return;

			stopping = true;
			wakeEvent.Set();

			// The host thread can be inside a long RPC (a large document still being parsed), so the
			// join is bounded. Abandoning it is safe: it only touches COM objects this host owns.
			if (!thread.Join(TimeSpan.FromSeconds(2)))
				return;

			// Report abandoned work instead of leaving callers waiting forever, and only release the
			// event once no thread can touch it again.
			while (workItems.TryDequeue(out var abandoned))
				abandoned.Abandon();

			wakeEvent.Dispose();
		}

		void Enqueue(WorkItem item)
		{
			// While shutting down only the host thread may queue follow-up work (teardown steps).
			if (stopping && !IsCurrentThread)
				throw new InvalidOperationException("The shell preview host has been disposed.");

			workItems.Enqueue(item);
			wakeEvent.Set();
		}

		void ThreadLoop()
		{
			PInvoke.OleInitialize();

			try
			{
				Span<HANDLE> handles = [new((nint)wakeEvent.SafeWaitHandle.DangerousGetHandle())];

				while (!stopping)
				{
					// Reset before draining, so work queued right after the drain still wakes the wait.
					wakeEvent.Reset();
					DrainWorkItems();

					if (stopping || !workItems.IsEmpty)
						continue;

					// Dispatches COM callbacks aimed at this apartment, plus this thread's window messages.
					PInvoke.CoWaitForMultipleHandles(
						(uint)(COWAIT_FLAGS.COWAIT_DISPATCH_WINDOW_MESSAGES | COWAIT_FLAGS.COWAIT_INPUTAVAILABLE),
						WaitTimeoutMilliseconds,
						handles,
						out _);
				}

				// Run whatever was queued before shutdown, so teardown is not silently dropped.
				DrainWorkItems();
			}
			finally
			{
				PInvoke.OleUninitialize();
			}
		}

		void DrainWorkItems()
		{
			// Items queued by a running item (chained teardown steps) are picked up by this same loop.
			while (workItems.TryDequeue(out var item))
				item.Invoke();
		}

		abstract class WorkItem
		{
			public abstract Task Task { get; }

			public abstract void Invoke();

			public abstract void Abandon();
		}

		sealed class ActionWorkItem(Action action) : WorkItem
		{
			readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public override Task Task => completion.Task;

			public override void Invoke()
			{
				try
				{
					action();
					completion.SetResult();
				}
				catch (Exception ex)
				{
					// A failed item must never take the pump loop down with it.
					completion.SetException(ex);
				}
			}

			public override void Abandon()
				=> completion.TrySetException(new ObjectDisposedException(nameof(ShellPreviewHost)));
		}

		sealed class FuncWorkItem<T>(Func<T> func) : WorkItem
		{
			readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public Task<T> Result => completion.Task;

			public override Task Task => completion.Task;

			public override void Invoke()
			{
				try
				{
					completion.SetResult(func());
				}
				catch (Exception ex)
				{
					completion.SetException(ex);
				}
			}

			public override void Abandon()
				=> completion.TrySetException(new ObjectDisposedException(nameof(ShellPreviewHost)));
		}
	}
}
