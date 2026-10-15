using CommandTerminal;
using DV.Logic.Job;
using DV.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevisedDVRoute
{
    public class RouteTaskChain
    {
        protected RouteTaskChain()
        {

        }

        public List<RouteTask> tasks;
        public RouteTaskChain nextTasks;

        public static RouteTaskChain FromDVJob(Job job)
        {
            if (job?.tasks == null || job.tasks.Count == 0)
                return null;

            if (job.tasks.Count == 1)
            {
                Task task = job.tasks[0];
                return FromDVTask(task);
            }

            return null;
        }

        public static RouteTaskChain FromDVTask(Task task)
        {
            if (task == null)
                return null;

            switch (task.InstanceTaskType)
            {
                case TaskType.Transport:
                case TaskType.Warehouse:
                    var singleRt = RouteTask.FromDVTask(task);
                    if (singleRt == null)
                        return null;
                    return new RouteTaskChain()
                    {
                        tasks = new List<RouteTask>()
                        {
                            singleRt
                        }
                    };

                case TaskType.Sequential:
                    var sData = task.GetTaskData();
                    if (sData?.nestedTasks == null)
                        return null;
                    RouteTaskChain first = null;
                    RouteTaskChain prev = null;
                    foreach (var nestedTask in sData.nestedTasks)
                    {
                        if (nestedTask == null || nestedTask.IsTaskCompleted())
                            continue;

                        RouteTaskChain current = null;

                        switch (nestedTask.InstanceTaskType)
                        {
                            case TaskType.Transport:
                            case TaskType.Warehouse:
                            case TaskType.Parallel:
                                current = FromDVTask(nestedTask); break;
                            default:
                                Terminal.Log($"Not supported task type {nestedTask.InstanceTaskType} inside sequence"); break;
                        }

                        if (current != null)
                        {
                            if (prev != null)
                            {
                                prev.nextTasks = current;
                            }

                            prev = current;
                        }

                        if (first == null)
                        {
                            first = current;
                        }
                    }
                    return first;
                case TaskType.Parallel:
                    var pData = task.GetTaskData();
                    if (pData?.nestedTasks == null)
                        return null;
                    RouteTaskChain result = new RouteTaskChain();
                    result.tasks = pData.nestedTasks
                        .Where(t => t != null && !t.IsTaskCompleted())
                        .Select(t => RouteTask.FromDVTask(t))
                        .Where(rt => rt != null)
                        .ToList();

                    return result;
            }

            return null;
        }

        public static RouteTaskChain FromDestination(Track destination, Trainset trainset)
        {
            return new RouteTaskChain()
            {
                tasks = new List<RouteTask>()
                {
                    RouteTask.FromDestination(destination, trainset)
                }
            };
        }

        public override string ToString()
        {
            var chain = this;
            StringBuilder sb = new StringBuilder();
            do
            {
                if (chain.tasks != null)
                {
                    chain.tasks.ForEach(t =>
                    {
                        sb.Append($"{t.DestinationTrack?.ID}({t.TrainSets?.Count}); ");
                    });
                }
                sb.Append("-> ");
                chain = chain.nextTasks;
            }
            while (chain != null);

            return sb.ToString();
        }
    }
    public class RouteTask
    {
        public Track DestinationTrack { get; protected set; }
        public Task DVTask { get; protected set; }
        public HashSet<Trainset> TrainSets { get; } = new HashSet<Trainset>();
        protected RouteTask()
        {

        }

        public static RouteTask FromDVTask(Task task)
        {
            if (task == null)
                return null;

            RouteTask newTask = new RouteTask();

#if DEBUG
            Terminal.Log($"job {task.Job?.ID} type {task.InstanceTaskType} state {task.state}");
#endif

            switch (task.InstanceTaskType)
            {
                case TaskType.Transport:
                    var transportTask = task as TransportTask;
                    if (transportTask == null) return null;
                    newTask.InitFromTransportTask(transportTask);
                    break;
                case TaskType.Warehouse:
                    var warehouseTask = task as WarehouseTask;
                    if (warehouseTask == null) return null;
                    newTask.InitFromWarehouseTask(warehouseTask);
                    break;
                default:
                    return null;
            }

            return newTask;
        }

        public static RouteTask FromDestination(Track destination, Trainset trainset)
        {
            RouteTask newTask = new RouteTask();
            newTask.DestinationTrack = destination;
            if (trainset != null)
                newTask.TrainSets.Add(trainset);

            return newTask;
        }

        private void GetTrainsets(List<Car> cars)
        {
            if (cars == null)
                return;

            cars.ForEach(car =>
            {
                if (car == null) return;
                TrainCar trainCar;
                if (!TrainCarRegistry.Instance.logicCarToTrainCar.TryGetValue(car, out trainCar))
                    return;

                if (trainCar?.trainset != null && !TrainSets.Contains(trainCar.trainset))
                    TrainSets.Add(trainCar.trainset);
            });
        }

        private void InitFromWarehouseTask(WarehouseTask task)
        {
            if (task == null) return;
            var taskData = task.GetTaskData();
            if (taskData == null) return;

            Terminal.Log($"warehouse task nested: {taskData.nestedTasks?.Count} cars: {taskData.cars?.Count}");

            DestinationTrack = taskData.destinationTrack;
            DVTask = task;
            GetTrainsets(taskData.cars);
        }

        private void InitFromTransportTask(TransportTask task)
        {
            if (task == null) return;
            var taskData = task.GetTaskData();
            if (taskData == null) return;

            Terminal.Log($"transport task nested: {taskData.nestedTasks?.Count} cars: {taskData.cars?.Count}");

            DestinationTrack = taskData.destinationTrack;
            DVTask = task;
            GetTrainsets(taskData.cars);
        }
    }
}
