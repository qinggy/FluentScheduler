﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentScheduler
{
    /// <summary>
    /// Controls the timer logic to execute all configured jobs.
    /// </summary>
    public static class JobManager
    {
        #region Internal fields

        private static bool _useUtc = false;

        private static Timer _timer = new Timer(state => ScheduleJobs(), null, Timeout.Infinite, Timeout.Infinite);

        private static ScheduleCollection _schedules = new ScheduleCollection();

        private static readonly ConcurrentDictionary<List<Action>, bool> _runningNonReentrant = new ConcurrentDictionary<List<Action>, bool>();

        private static readonly ConcurrentDictionary<Guid, Schedule> _running = new ConcurrentDictionary<Guid, Schedule>();

        private static DateTime Now
        {
            get
            {
                return _useUtc ? DateTime.UtcNow : DateTime.Now;
            }
        }

        #endregion

        #region Job factory

        private static IJobFactory _jobFactory;

        public static IJobFactory JobFactory
        {
            get
            {
                return (_jobFactory = _jobFactory ?? new JobFactory());
            }
            set
            {
                _jobFactory = value;
            }
        }

        #endregion

        #region Event handling

        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly",
            Justification = "Using strong-typed GenericEventHandler<TSender, TEventArgs> event handler pattern.")]
        public static event GenericEventHandler<JobExceptionInfo, FluentScheduler.UnhandledExceptionEventArgs> JobException;

        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly",
            Justification = "Using strong-typed GenericEventHandler<TSender, TEventArgs> event handler pattern.")]
        public static event GenericEventHandler<JobStartInfo, EventArgs> JobStart;

        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly",
            Justification = "Using strong-typed GenericEventHandler<TSender, TEventArgs> event handler pattern.")]
        public static event GenericEventHandler<JobEndInfo, EventArgs> JobEnd;

        private static void RaiseJobException(Schedule schedule, Task t)
        {
            var handler = JobException;
            if (handler != null && t.Exception != null)
            {
                var info = new JobExceptionInfo
                {
                    Name = schedule.Name,
                    Task = t
                };
                handler(info, new FluentScheduler.UnhandledExceptionEventArgs(t.Exception.InnerException, true));
            }
        }
        private static void RaiseJobStart(Schedule schedule, DateTime startTime)
        {
            var handler = JobStart;
            if (handler != null)
            {
                var info = new JobStartInfo
                {
                    Name = schedule.Name,
                    StartTime = startTime
                };
                handler(info, new EventArgs());
            }
        }
        private static void RaiseJobEnd(Schedule schedule, DateTime startTime, TimeSpan duration)
        {
            var handler = JobEnd;
            if (handler != null)
            {
                var info = new JobEndInfo
                {
                    Name = schedule.Name,
                    StartTime = startTime,
                    Duration = duration
                };
                if (schedule.NextRun != default(DateTime))
                    info.NextRun = schedule.NextRun;

                handler(info, new EventArgs());
            }
        }

        #endregion

        #region Start, stop & initialize

        /// <summary>
        /// Initializes the job manager with all schedules configured in the specified registry
        /// </summary>
        /// <param name="registry">Registry containing job schedules</param>
        public static void Initialize(Registry registry)
        {
            if (registry == null)
                throw new ArgumentNullException("registry");

            _useUtc = registry.UtcTime;
            CalculateNextRun(registry.Schedules).ToList().ForEach(RunJob);
            ScheduleJobs();
        }

        /// <summary>
        /// Restarts the job manager if it had previously been stopped
        /// </summary>
        public static void Start()
        {
            ScheduleJobs();
        }

        /// <summary>
        /// Stops the job manager from executing jobs.
        /// </summary>
        public static void Stop()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        #endregion

        #region Exposing schedules

        /// <summary>
        /// Get schedule by name.
        /// </summary>
        /// <param name="name">Schedule name</param>
        /// <returns>Schedule instance or null if the schedule does not exist</returns>
        public static Schedule GetSchedule(string name)
        {
            return _schedules.Get(name);
        }

        /// <summary>
        /// Gets a list of currently schedules currently executing.
        /// </summary>
        public static IEnumerable<Schedule> RunningSchedules
        {
            get
            {
                return _running.Values.ToList();
            }
        }

        /// <summary>
        /// The list of all schedules, whether or not they are currently running.
        /// Use <see cref="GetSchedule"/> to get concrete schedule by name.
        /// </summary>
        public static IEnumerable<Schedule> AllSchedules
        {
            get
            {
                return _schedules.All();
            }
        }

        #endregion

        #region Exposing adding & removing jobs (without the registry)

        /// <summary>
        /// Adds a job to the job manager
        /// </summary>
        /// <typeparam name="T">Job to schedule</typeparam>
        /// <param name="jobSchedule">Schedule for the job</param>
        [SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter",
            Justification = "The 'T' requirement is on purpose.")]
        public static void AddJob<T>(Action<Schedule> jobSchedule) where T : IJob
        {
            if (jobSchedule == null)
                throw new ArgumentNullException("jobSchedule");

            AddJob(jobSchedule, new Schedule(JobFactory.GetJobInstance<T>()) { Name = typeof(T).Name });
        }

        /// <summary>
        /// Adds a job to the job manager
        /// </summary>
        /// <param name="jobAction">Job to schedule</param>
        /// <param name="jobSchedule">Schedule for the job</param>
        public static void AddJob(Action jobAction, Action<Schedule> jobSchedule)
        {
            if (jobSchedule == null)
                throw new ArgumentNullException("jobSchedule");

            AddJob(jobSchedule, new Schedule(jobAction));
        }

        private static void AddJob(Action<Schedule> jobSchedule, Schedule schedule)
        {
            jobSchedule(schedule);
            CalculateNextRun(new Schedule[] { schedule }).ToList().ForEach(RunJob);
            ScheduleJobs();
        }

        public static void RemoveJob(string name)
        {
            _schedules.Remove(name);
        }

        #endregion

        #region Calculating, scheduling & running

        private static IEnumerable<Schedule> CalculateNextRun(IEnumerable<Schedule> schedules)
        {
            foreach (var schedule in schedules)
            {
                if (schedule.CalculateNextRun == null)
                {
                    if (schedule.DelayRunFor > TimeSpan.Zero)
                    {
                        // delayed job
                        schedule.NextRun = Now.Add(schedule.DelayRunFor);
                        _schedules.Add(schedule);
                    }
                    else
                    {
                        // run immediately
                        yield return schedule;
                    }
                    var hasAdded = false;
                    foreach (var child in schedule.AdditionalSchedules.Where(x => x.CalculateNextRun != null))
                    {
                        var nextRun = child.CalculateNextRun(Now.Add(child.DelayRunFor).AddMilliseconds(1));
                        if (!hasAdded || schedule.NextRun > nextRun)
                        {
                            schedule.NextRun = nextRun;
                            hasAdded = true;
                        }
                    }
                }
                else
                {
                    schedule.NextRun = schedule.CalculateNextRun(Now.Add(schedule.DelayRunFor));
                    _schedules.Add(schedule);
                }

                foreach (var childSchedule in schedule.AdditionalSchedules)
                {
                    if (childSchedule.CalculateNextRun == null)
                    {
                        if (childSchedule.DelayRunFor > TimeSpan.Zero)
                        {
                            // delayed job
                            childSchedule.NextRun = Now.Add(childSchedule.DelayRunFor);
                            _schedules.Add(childSchedule);
                        }
                        else
                        {
                            // run immediately
                            yield return childSchedule;
                            continue;
                        }
                    }
                    else
                    {
                        childSchedule.NextRun = childSchedule.CalculateNextRun(Now.Add(schedule.DelayRunFor));
                        _schedules.Add(childSchedule);
                    }
                }
            }
        }

        private static void ScheduleJobs()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _schedules.Sort();

            if (!_schedules.Any())
                return;

            var firstJob = _schedules.First();
            if (firstJob.NextRun <= Now)
            {
                RunJob(firstJob);
                if (firstJob.CalculateNextRun == null)
                {
                    // probably a ToRunNow().DelayFor() job, there's no CalculateNextRun
                }
                else
                {
                    firstJob.NextRun = firstJob.CalculateNextRun(Now.AddMilliseconds(1));
                }

                if (firstJob.NextRun <= Now || firstJob.PendingRunOnce)
                {
                    _schedules.Remove(firstJob);
                }

                firstJob.PendingRunOnce = false;
                ScheduleJobs();
                return;
            }

            var timerInterval = firstJob.NextRun - Now;
            if (timerInterval <= TimeSpan.Zero)
            {
                ScheduleJobs();
                return;
            }

            _timer.Change(timerInterval, timerInterval);
        }

        internal static void RunJob(Schedule schedule)
        {
            if (schedule.Disabled)
                return;

            if (!schedule.Reentrant)
            {
                if (!_runningNonReentrant.TryAdd(schedule.Jobs, true))
                    return;
            }

            var id = Guid.NewGuid();
            _running.TryAdd(id, schedule);

            var start = Now;
            RaiseJobStart(schedule, start);
            var task = Task.Factory.StartNew(() =>
            {
                var stopwatch = new Stopwatch();
                try
                {
                    stopwatch.Start();
                    foreach (var action in schedule.Jobs)
                    {
                        var subTask = Task.Factory.StartNew(action);
                        subTask.Wait();
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    bool notUsed;
                    _runningNonReentrant.TryRemove(schedule.Jobs, out notUsed);
                    Schedule notUsedSchedule;
                    _running.TryRemove(id, out notUsedSchedule);
                    RaiseJobEnd(schedule, start, stopwatch.Elapsed);
                }
            }, TaskCreationOptions.PreferFairness);
            task.ContinueWith(t => RaiseJobException(schedule, t), TaskContinuationOptions.OnlyOnFaulted);
        }

        #endregion
    }
}
