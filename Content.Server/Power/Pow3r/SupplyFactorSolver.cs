using Pidgin;
using Robust.Shared.Utility;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using static Content.Server.Power.Pow3r.PowerState;

namespace Content.Server.Power.Pow3r
{
    public sealed class SupplyFactorSolver : IPowerSolver
    {
        private sealed class HeightComparer : Comparer<Network>
        {
            public static HeightComparer Instance { get; } = new();

            public override int Compare(Network? x, Network? y)
            {
                if (x!.Height == y!.Height) return 0;
                if (x!.Height > y!.Height) return 1;
                return -1;
            }
        }

        public void Tick(float frameTime, PowerState state, int parallel)
        {
            ClearLoadsAndSupplies(state);

            state.GroupedNets ??= GroupByNetworkDepth(state);
            DebugTools.Assert(state.GroupedNets.Select(x => x.Count).Sum() == state.Networks.Count);

            // Each network height layer can be run in parallel without issues.
            var opts = new ParallelOptions { MaxDegreeOfParallelism = parallel };
            foreach (var group in state.GroupedNets)
            {
                // Note that many net-layers only have a handful of networks.
                // E.g., the number of nets from lowest to highest for box and saltern are:
                // Saltern: 1477, 11, 2, 2, 3.
                // Box:     3308, 20, 1, 5.
                //
                // I have NFI what the overhead for a Parallel.ForEach is, and how it compares to computing differently
                // sized nets. Basic benchmarking shows that this is better, but maybe the highest-tier nets should just
                // be run sequentially? But then again, maybe they are 2-3 very BIG networks at the top? So maybe:
                //
                // TODO make GroupByNetworkDepth evaluate the TOTAL size of each layer (i.e. loads + chargers +
                // suppliers + discharger) Then decide based on total layer size whether its worth parallelizing that
                // layer?
                Parallel.ForEach(group, opts, net => UpdateNetwork(net, state, frameTime));
            }

            ClearBatteries(state);

            PowerSolverShared.UpdateRampPositions(frameTime, state);
        }

        private void ClearLoadsAndSupplies(PowerState state)
        {
            foreach (var load in state.Loads.Values)
            {
                if (load.Paused)
                    continue;

                load.ReceivingPower = 0;
            }

            foreach (var supply in state.Supplies.Values)
            {
                if (supply.Paused)
                    continue;

                supply.CurrentSupply = 0;
                supply.SupplyRampTarget = 0;
            }
        }

        private void UpdateNetwork(Network network, PowerState state, float frameTime)
        {
            // Greater than 1 if supply exceeds demand, less than 1 if demand exceeds supply. Both
            // supplies and batteries do their best to keep this number at 1.
            float supplyRatio;
            if (network.LastCombinedDemand != 0)
                supplyRatio = network.LastCombinedSupply / network.LastCombinedDemand;
            else
                supplyRatio = 1f;

            // Add up fixed loads. We have no control over their demands.
            var demand = 0f;
            foreach (var loadId in network.Loads)
            {
                var load = state.Loads[loadId];
                if (!load.Enabled || load.Paused)
                    continue;

                DebugTools.Assert(load.DesiredPower >= 0);
                demand += load.DesiredPower;
                load.ReceivingPower = load.DesiredPower * supplyRatio;
            }

            // Add up supplies. Apply negative feedback to supply ramp position.
            var totalSupply = 0f;
            var totalMaxSupply = 0f;
            foreach (var supplyId in network.Supplies)
            {
                var supply = state.Supplies[supplyId];
                if (!supply.Enabled || supply.Paused)
                    continue;

                var rampMax = supply.SupplyRampPosition + supply.SupplyRampTolerance;
                var effectiveSupply = Math.Min(rampMax, supply.MaxSupply);

                DebugTools.Assert(effectiveSupply >= 0);
                DebugTools.Assert(supply.MaxSupply >= 0);

                supply.AvailableSupply = effectiveSupply;
                supply.CurrentSupply = supply.AvailableSupply;
                totalSupply += effectiveSupply;
                totalMaxSupply += supply.MaxSupply;

                float alpha = 0.1f;
                supply.SupplyRampTarget *= (1f - supplyRatio) * alpha * frameTime;
                supply.SupplyRampTarget = Math.Clamp(supply.SupplyRampTarget, 0, supply.MaxSupply);
            }

            // Run through all batteries.
            var totalBatterySupply = 0f;
            var totalMaxBatterySupply = 0f;
            foreach (var batteryId in network.BatteryLoads)
            {
                var battery = state.Batteries[batteryId];
                if (!battery.Enabled || battery.Paused)
                    continue;

                if (battery.CanCharge)
                {
                    float alpha = 0.1f;
                    battery.DesiredPower *= (supplyRatio - 1f) * alpha * frameTime;
                    battery.DesiredPower = Math.Clamp(battery.DesiredPower, 0, battery.MaxChargeRate);
                    DebugTools.Assert(battery.DesiredPower >= 0);
                    demand += battery.DesiredPower;

                    battery.LoadingMarked = true;
                    battery.CurrentReceiving = battery.DesiredPower * supplyRatio;
                    battery.CurrentStorage += frameTime * battery.CurrentReceiving * battery.Efficiency;
                    DebugTools.Assert(battery.CurrentStorage <= battery.Capacity || MathHelper.CloseTo(battery.CurrentStorage, battery.Capacity, 1e-5));
                    battery.CurrentStorage = MathF.Min(battery.CurrentStorage, battery.Capacity);
                }

                if (battery.CanDischarge)
                {
                    battery.SupplyingMarked = true;
                    battery.CurrentSupply = battery.AvailableSupply;
                    float dC = frameTime * battery.CurrentSupply;
                    battery.CurrentStorage -= dC;
                    battery.CurrentStorage = MathF.Max(0, battery.CurrentStorage);

                    float alpha = 0.1f;
                    battery.SupplyRampTarget *= (1f - supplyRatio) * alpha * frameTime;
                    battery.SupplyRampTarget = Math.Clamp(battery.SupplyRampTarget, 0, battery.MaxEffectiveSupply);

                    var supplyCap = Math.Min(battery.MaxSupply, battery.SupplyRampPosition + battery.SupplyRampTolerance);
                    var supplyAndPassthrough = supplyCap + battery.CurrentReceiving * battery.Efficiency;

                    battery.AvailableSupply = supplyAndPassthrough;
                    battery.MaxEffectiveSupply = Math.Min(dC, battery.MaxSupply + battery.CurrentReceiving * battery.Efficiency);
                    totalBatterySupply += battery.AvailableSupply;
                    totalMaxBatterySupply += battery.MaxEffectiveSupply;
                }
            }

            network.LastCombinedSupply = totalSupply + totalBatterySupply;
            network.LastCombinedMaxSupply = totalMaxSupply + totalMaxBatterySupply;
            network.LastCombinedDemand = demand;
        }

        private void ClearBatteries(PowerState state)
        {
            // Clear supplying/loading on any batteries that haven't been marked by usage.
            // Because we need this data while processing ramp-pegging, we can't clear it at the start.
            foreach (var battery in state.Batteries.Values)
            {
                if (battery.Paused)
                    continue;

                if (!battery.SupplyingMarked)
                {
                    battery.CurrentSupply = 0;
                    battery.SupplyRampTarget = 0;
                    battery.LoadingNetworkDemand = 0;
                }

                if (!battery.LoadingMarked)
                {
                    battery.CurrentReceiving = 0;
                }

                battery.SupplyingMarked = false;
                battery.LoadingMarked = false;
            }
        }

        private List<List<Network>> GroupByNetworkDepth(PowerState state)
        {
            List<List<Network>> groupedNetworks = new();
            foreach (var network in state.Networks.Values)
            {
                network.Height = -1;
            }

            foreach (var network in state.Networks.Values)
            {
                if (network.Height == -1)
                    RecursivelyEstimateNetworkDepth(state, network, groupedNetworks);
            }

            return groupedNetworks;
        }

        private static void RecursivelyEstimateNetworkDepth(PowerState state, Network network, List<List<Network>> groupedNetworks)
        {
            network.Height = -2;
            var height = -1;

            foreach (var batteryId in network.BatteryLoads)
            {
                var battery = state.Batteries[batteryId];

                if (battery.LinkedNetworkDischarging == default || battery.LinkedNetworkDischarging == network.Id)
                    continue;

                var subNet = state.Networks[battery.LinkedNetworkDischarging];
                if (subNet.Height == -1)
                    RecursivelyEstimateNetworkDepth(state, subNet, groupedNetworks);
                else if (subNet.Height == -2)
                {
                    // this network is currently computing its own height (we encountered a loop).
                    continue;
                }

                height = Math.Max(subNet.Height, height);
            }

            network.Height = 1 + height;

            if (network.Height >= groupedNetworks.Count)
                groupedNetworks.Add(new() { network });
            else
                groupedNetworks[network.Height].Add(network);
        }
    }
}
