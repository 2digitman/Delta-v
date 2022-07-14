using JetBrains.Annotations;
using Robust.Shared.Timing;
using Content.Server.Medical.Components;
using Content.Server.Cloning.Components;
using Content.Server.Power.Components;
using Content.Server.Mind.Components;
using Content.Server.MachineLinking.System;
using Content.Server.MachineLinking.Events;
using Content.Server.UserInterface;
using Content.Shared.MobState.Components;
using Content.Server.MobState;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Content.Shared.Cloning.CloningConsole;
using Content.Shared.Cloning;
using Content.Shared.MachineLinking.Events;

namespace Content.Server.Cloning.Systems
{
    [UsedImplicitly]
    public sealed class CloningConsoleSystem : EntitySystem
    {
        [Dependency] private readonly SignalLinkerSystem _signalSystem = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly CloningSystem _cloningSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
        [Dependency] private readonly MobStateSystem _mobStateSystem = default!;
        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<CloningConsoleComponent, ComponentInit>(OnInit);
            SubscribeLocalEvent<CloningConsoleComponent, UiButtonPressedMessage>(OnButtonPressed);
            SubscribeLocalEvent<CloningConsoleComponent, AfterActivatableUIOpenEvent>(OnUIOpen);
            SubscribeLocalEvent<CloningConsoleComponent, PowerChangedEvent>(OnPowerChanged);
            SubscribeLocalEvent<CloningConsoleComponent, NewLinkEvent>(OnNewLink);
            SubscribeLocalEvent<CloningConsoleComponent, PortDisconnectedEvent>(OnPortDisconnected);
            SubscribeLocalEvent<CloningConsoleComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        }

        private void OnInit(EntityUid uid, CloningConsoleComponent component, ComponentInit args)
        {
            _signalSystem.EnsureTransmitterPorts(uid, component.ScannerPort, component.PodPort);
        }
        private void OnButtonPressed(EntityUid uid, CloningConsoleComponent consoleComponent, UiButtonPressedMessage args)
        {
            if (!consoleComponent.Powered)
                return;

            switch (args.Button)
            {
                case UiButton.Clone:
                    if (consoleComponent.GeneticScanner != null && consoleComponent.CloningPod != null)
                        TryClone(uid, consoleComponent.CloningPod.Value, consoleComponent.GeneticScanner.Value, consoleComponent: consoleComponent);
                    break;
                case UiButton.Eject:
                    if (consoleComponent.CloningPod != null)
                        TryEject(uid, consoleComponent.CloningPod.Value, consoleComponent: consoleComponent);
                    break;
            }
            UpdateUserInterface(consoleComponent);
        }

        private void OnPowerChanged(EntityUid uid, CloningConsoleComponent component, PowerChangedEvent args)
        {
            component.Powered = args.Powered;
            UpdateUserInterface(component);
        }

        private void OnNewLink(EntityUid uid, CloningConsoleComponent component, NewLinkEvent args)
        {
            Logger.Error("args.TransmitterPort is " + args.TransmitterPort);
            Logger.Error("args.Receiver is: " + args.Receiver);
            if (TryComp<MedicalScannerComponent>(args.Receiver, out var scanner) && args.TransmitterPort == "MedicalScannerSender")
            {
                Logger.Error("Adding scanner...");
                component.GeneticScanner = args.Receiver;
                scanner.ConnectedConsole = uid;
            }

            if (TryComp<CloningPodComponent>(args.Receiver, out var pod) && args.TransmitterPort == "CloningPodSender")
            {
                Logger.Error("Adding pod...");
                component.CloningPod = args.Receiver;
                pod.ConnectedConsole = uid;
            }
            RecheckConnections(uid, component.CloningPod, component.GeneticScanner, component);
        }

        private void OnPortDisconnected(EntityUid uid, CloningConsoleComponent component, PortDisconnectedEvent args)
        {
            if (args.Port == "MedicalScannerSender")
                component.GeneticScanner = null;

            if (args.Port == "CloningPodSender")
                component.CloningPod = null;

            UpdateUserInterface(component);
        }

        private void OnUIOpen(EntityUid uid, CloningConsoleComponent component, AfterActivatableUIOpenEvent args)
        {
            UpdateUserInterface(component);
        }

        private void OnAnchorChanged(EntityUid uid, CloningConsoleComponent component, ref AnchorStateChangedEvent args)
        {
            if (args.Anchored)
            {
                RecheckConnections(uid, component.CloningPod, component.GeneticScanner, component);
                return;
            }
            UpdateUserInterface(component);
        }

        public void UpdateUserInterface(CloningConsoleComponent consoleComponent)
        {
            if (!consoleComponent.Powered)
            {
                _uiSystem.GetUiOrNull(consoleComponent.Owner, CloningConsoleUiKey.Key)?.CloseAll();
                return;
            }

            var newState = GetUserInterfaceState(consoleComponent);

            _uiSystem.GetUiOrNull(consoleComponent.Owner, CloningConsoleUiKey.Key)?.SetState(newState);
        }

        public void TryEject(EntityUid uid, EntityUid clonePodUid, CloningPodComponent? cloningPod = null, CloningConsoleComponent? consoleComponent = null)
        {
            if (!Resolve(uid, ref consoleComponent) || !Resolve(clonePodUid, ref cloningPod))
                return;

            _cloningSystem.Eject(clonePodUid, cloningPod);
        }

        public void TryClone(EntityUid uid, EntityUid cloningPodUid, EntityUid scannerUid, CloningPodComponent? cloningPod = null, MedicalScannerComponent? scannerComp = null, CloningConsoleComponent? consoleComponent = null)
        {
            if (!Resolve(uid, ref consoleComponent) || !Resolve(cloningPodUid, ref cloningPod)  || !Resolve(scannerUid, ref scannerComp))
                return;

            if (!Transform(cloningPodUid).Anchored || !Transform(scannerUid).Anchored)
                return;

            if (!consoleComponent.CloningPodInRange || !consoleComponent.GeneticScannerInRange)
                return;

            if (scannerComp.BodyContainer.ContainedEntity is null)
                return;

            if (!TryComp<MindComponent>(scannerComp.BodyContainer.ContainedEntity.Value, out var mindComp))
                return;

            var mind = mindComp.Mind;

            if (mind == null || mind.UserId.HasValue == false || mind.Session == null)
                return;

            bool cloningSuccessful = _cloningSystem.TryCloning(cloningPodUid, scannerComp.BodyContainer.ContainedEntity.Value, mind, cloningPod);
        }

        public void RecheckConnections(EntityUid console, EntityUid? cloningPod, EntityUid? scanner, CloningConsoleComponent? consoleComp = null)
        {
            if (!Resolve(console, ref consoleComp))
                return;

            if (scanner != null)
            {
                Transform((EntityUid) scanner).Coordinates.TryDistance(EntityManager, Transform((console)).Coordinates, out float scannerDistance);
                consoleComp.GeneticScannerInRange = scannerDistance <= consoleComp.MaxDistance;
            }
            if (cloningPod != null)
            {
                Transform((EntityUid) cloningPod).Coordinates.TryDistance(EntityManager, Transform((console)).Coordinates, out float podDistance);
                consoleComp.CloningPodInRange = podDistance <= consoleComp.MaxDistance;
            }

            UpdateUserInterface(consoleComp);
        }
        private CloningConsoleBoundUserInterfaceState GetUserInterfaceState(CloningConsoleComponent consoleComponent)
        {
            ClonerStatus clonerStatus = ClonerStatus.Ready;

            // genetic scanner info
            string scanBodyInfo = "Unknown";
            bool scannerConnected = false;
            bool scannerInRange = consoleComponent.GeneticScannerInRange;
            if (consoleComponent.GeneticScanner != null && TryComp<MedicalScannerComponent>(consoleComponent.GeneticScanner, out var scanner)) {

                scannerConnected = true;
                EntityUid? scanBody = scanner.BodyContainer.ContainedEntity;

                // GET STATE
                if (scanBody == null || !HasComp<MobStateComponent>(scanBody))
                    clonerStatus = ClonerStatus.ScannerEmpty;
                else
                {
                    scanBodyInfo = MetaData((EntityUid) scanBody).EntityName;

                    TryComp<MindComponent>(scanBody, out var mindComp);

                    if (!_mobStateSystem.IsDead((EntityUid) scanBody))
                    {
                        clonerStatus = ClonerStatus.ScannerOccupantAlive;
                    }
                    else
                    {
                        if (mindComp == null || mindComp.Mind == null || mindComp.Mind.UserId == null || !_playerManager.TryGetSessionById(mindComp.Mind.UserId.Value, out var client))
                        {
                            clonerStatus = ClonerStatus.NoMindDetected;
                        }
                    }
                }
            }

            // cloning pod info
            var cloneBodyInfo = "Unknown";
            float cloningProgress = 0;
            float cloningTime = 30f;
            bool clonerProgressing = false;
            bool clonerConnected = false;
            bool clonerMindPresent = false;
            bool clonerInRange = consoleComponent.CloningPodInRange;
            if (consoleComponent.CloningPod != null && TryComp<CloningPodComponent>(consoleComponent.CloningPod, out var clonePod)
            && Transform((EntityUid) consoleComponent.CloningPod).Anchored)
            {
                clonerConnected = true;
                EntityUid? cloneBody = clonePod.BodyContainer.ContainedEntity;

                cloningProgress = clonePod.CloningProgress;
                cloningTime = clonePod.CloningTime;
                clonerProgressing = _cloningSystem.IsPowered(clonePod) && (clonePod.BodyContainer.ContainedEntity != null);
                clonerMindPresent = clonePod.Status == CloningPodStatus.Cloning;
                if (cloneBody != null)
                {
                    cloneBodyInfo = MetaData((EntityUid) cloneBody).EntityName;
                    clonerStatus = ClonerStatus.ClonerOccupied;
                }
            }
            else
            {
                clonerStatus = ClonerStatus.NoClonerDetected;
            }

            return new CloningConsoleBoundUserInterfaceState(
                scanBodyInfo,
                cloneBodyInfo,
                _gameTiming.CurTime,
                cloningProgress,
                cloningTime,
                clonerProgressing,
                clonerMindPresent,
                clonerStatus,
                scannerConnected,
                scannerInRange,
                clonerConnected,
                clonerInRange
                );
        }
    }
}
