#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.System;
using Uno.Disposables;
using Uno.Extensions;
using Uno.Logging;

namespace Windows.UI.Input
{
	public partial class GestureRecognizer
	{
		// Note: this is also responsible to handle "Drag manipulations"
		internal class Manipulation
		{
			internal static readonly Thresholds StartTouch = new Thresholds { TranslateX = 15, TranslateY = 15, Rotate = 5, Expansion = 15 };
			internal static readonly Thresholds StartPen = new Thresholds { TranslateX = 15, TranslateY = 15, Rotate = 5, Expansion = 15 };
			internal static readonly Thresholds StartMouse = new Thresholds { TranslateX = 1, TranslateY = 1, Rotate = .1, Expansion = 1 };

			internal static readonly Thresholds DeltaTouch = new Thresholds { TranslateX = 2, TranslateY = 2, Rotate = .1, Expansion = 1 };
			internal static readonly Thresholds DeltaPen = new Thresholds { TranslateX = 2, TranslateY = 2, Rotate = .1, Expansion = 1 };
			internal static readonly Thresholds DeltaMouse = new Thresholds { TranslateX = 1, TranslateY = 1, Rotate = .1, Expansion = 1 };

			// Inertia thresholds are expressed in 'unit / millisecond' (unit being either 'logical px' or 'degree')
			internal static readonly Thresholds InertiaTouch = new Thresholds { TranslateX = 15d/1000, TranslateY = 15d/1000, Rotate = 5d/1000, Expansion = 15d/1000 };
			internal static readonly Thresholds InertiaPen = new Thresholds { TranslateX = 15d/1000, TranslateY = 15d/1000, Rotate = 5d/1000, Expansion = 15d/1000 };
			internal static readonly Thresholds InertiaMouse = new Thresholds { TranslateX = 15d/1000, TranslateY = 15d/1000, Rotate = 5d/1000, Expansion = 15d/1000 };

			private enum ManipulationState
			{
				Starting = 1,
				Started = 2,
				Inertia = 3,
				Completed = 4,
			}

			private readonly GestureRecognizer _recognizer;
			private readonly PointerDeviceType _deviceType;
			private readonly GestureSettings _settings;
			private bool _isDraggingEnable; // Note: This might get disabled if user moves out of range while initial hold delay with finger
			private readonly bool _isTranslateXEnabled;
			private readonly bool _isTranslateYEnabled;
			private readonly bool _isRotateEnabled;
			private readonly bool _isScaleEnabled;

			private readonly Thresholds _startThresholds;
			private readonly Thresholds _deltaThresholds;
			private readonly Thresholds _inertiaThresholds;

			private ManipulationState _state = ManipulationState.Starting;
			private Points _origins;
			private Points _currents;
			private (ushort onStart, ushort current) _contacts;
			private (ManipulationDelta sumOfDelta, ulong timestamp, ushort contacts) _lastPublishedState = (ManipulationDelta.Empty, 0, 0); // Note: not maintained for dragging manipulation
			private ManipulationVelocities _lastRelevantVelocities;
			private InertiaProcessor? _inertia;

			public bool IsDragManipulation { get; private set; }

			public Manipulation(GestureRecognizer recognizer, PointerPoint pointer1)
			{
				_recognizer = recognizer;
				_deviceType = pointer1.PointerDevice.PointerDeviceType;

				_origins = _currents = pointer1;
				_contacts = (0, 1);

				switch (_deviceType)
				{
					case PointerDeviceType.Mouse:
						_startThresholds = StartMouse;
						_deltaThresholds = DeltaMouse;
						_inertiaThresholds = InertiaMouse;
						break;

					case PointerDeviceType.Pen:
						_startThresholds = StartPen;
						_deltaThresholds = DeltaPen;
						_inertiaThresholds = InertiaPen;
						break;

					default:
					case PointerDeviceType.Touch:
						_startThresholds = StartTouch;
						_deltaThresholds = DeltaTouch;
						_inertiaThresholds = InertiaTouch;
						break;
				}

				var args = new ManipulationStartingEventArgs(_recognizer._gestureSettings);
				_recognizer.ManipulationStarting?.Invoke(_recognizer, args);
				_settings = args.Settings;

				if ((_settings & (GestureSettingsHelper.Manipulations | GestureSettingsHelper.DragAndDrop)) == 0)
				{
					_state = ManipulationState.Completed;
				}
				else
				{
					_isDraggingEnable = (_settings & GestureSettings.Drag) != 0;
					_isTranslateXEnabled = (_settings & (GestureSettings.ManipulationTranslateX | GestureSettings.ManipulationTranslateRailsX)) != 0;
					_isTranslateYEnabled = (_settings & (GestureSettings.ManipulationTranslateY | GestureSettings.ManipulationTranslateRailsY)) != 0;
					_isRotateEnabled = (_settings & GestureSettings.ManipulationRotate) != 0;
					_isScaleEnabled = (_settings & GestureSettings.ManipulationScale) != 0;
				}
			}

			public bool IsActive(PointerDeviceType type, uint id)
				=> _state != ManipulationState.Completed
					&& _deviceType == type
					&& _origins.ContainsPointer(id);

			public bool TryAdd(PointerPoint point)
			{
				if (_state >= ManipulationState.Inertia)
				{
					// A new manipulation has to be started
					return false;
				}
				else if (point.PointerDevice.PointerDeviceType != _deviceType
					|| _currents.HasPointer2)
				{
					// A manipulation is already active, but cannot handle this new pointer.
					// We don't support multiple active manipulation on a single element / gesture recognizer,
					// so even if we don't effectively add the given pointer to the active manipulation,
					// we return true to make sure that the current manipulation is not Completed.
					return true;
				}

				_origins.SetPointer2(point);
				_currents.SetPointer2(point);
				_contacts.current++;

				// We force to start the manipulation (or update it) as soon as a second pointer is pressed
				NotifyUpdate();

				return true;
			}

			public void Update(IList<PointerPoint> updated)
			{
				if (_state >= ManipulationState.Inertia)
				{
					// We no longer track pointers (even pointer2) once inertia has started
					return;
				}

				var hasUpdate = false;
				foreach (var point in updated)
				{
					hasUpdate |= TryUpdate(point);
				}

				if (hasUpdate)
				{
					NotifyUpdate();
				}
			}

			public void Remove(PointerPoint removed)
			{
				if (_state >= ManipulationState.Inertia)
				{
					// We no longer track pointers (even pointer2) once inertia has started
					return;
				}

				if (TryUpdate(removed))
				{
					_contacts.current--;

					NotifyUpdate();
				}
			}

			public void Complete()
			{
				// If the manipulation was not started, we just abort the manipulation without any event
				switch (_state)
				{
					case ManipulationState.Started when IsDragManipulation:
						_inertia?.Dispose(); // Safety, inertia should never been started when IsDragManipulation, especially if _state is ManipulationState.Started ^^
						_state = ManipulationState.Completed;

						_recognizer.Dragging?.Invoke(
							_recognizer,
							new DraggingEventArgs(_currents.Pointer1, DraggingState.Completed, _contacts.onStart));
						break;

					case ManipulationState.Started:
					case ManipulationState.Inertia:
						_inertia?.Dispose();
						_state = ManipulationState.Completed;

						var cumulative = GetCumulative();
						var delta = GetDelta(cumulative);
						var velocities = GetVelocities(delta);

						_recognizer.ManipulationCompleted?.Invoke(
							_recognizer,
							new ManipulationCompletedEventArgs(_deviceType, _currents.Center, cumulative, velocities, _state == ManipulationState.Inertia, _contacts.onStart, _contacts.current));
						break;

					default:
						_inertia?.Dispose();
						_state = ManipulationState.Completed;
						break;
				}

				// Self scavenge our self from the _recognizer ... yes it's a bit strange,
				// but it's the safest and easiest way to avoid invalid state.
				if (_recognizer._manipulation == this)
				{
					_recognizer._manipulation = null;
					_recognizer.TryCancelHapticFeedbackTimer();
				}
			}

			private bool TryUpdate(PointerPoint point)
			{
				if (_deviceType != point.PointerDevice.PointerDeviceType)
				{
					return false;
				}

				return _currents.TryUpdate(point);
			}

			private void NotifyUpdate()
			{
				// Note: Make sure to update the _sumOfPublishedDelta before raising the event, so if an exception is raised
				//		 or if the manipulation is Completed, the Complete event args can use the updated _sumOfPublishedDelta.

				var cumulative = GetCumulative();
				var delta = GetDelta(cumulative);
				var velocities = GetVelocities(delta);
				var pointerAdded = _contacts.current > _lastPublishedState.contacts;
				var pointerRemoved = _contacts.current < _lastPublishedState.contacts;

				switch (_state)
				{
					case ManipulationState.Starting when IsBeginningOfDragManipulation():
						// On UWP if the element was configured to allow both Drag and Manipulations,
						// both events are going to be raised (... until the drag "content" is being render an captures all pointers).
						// This results as a manipulation started which is never completed.
						// If user uses double touch the manipulation will however start and complete when user adds / remove the 2nd finger.
						// On Uno, as allowing both Manipulations and drop on the same element is really a stretch case (and is bugish on UWP),
						// we accept as a known limitation that once dragging started no manipulation event would be fired.
						_state = ManipulationState.Started;
						_contacts.onStart = _contacts.current;
						IsDragManipulation = true;

						_recognizer.Dragging?.Invoke(
							_recognizer,
							new DraggingEventArgs(_currents.Pointer1, DraggingState.Started, _contacts.onStart));
						break;

					case ManipulationState.Starting when pointerAdded:
						_state = ManipulationState.Started;
						_contacts.onStart = _contacts.current;

						UpdatePublishedState(cumulative);
						_recognizer.ManipulationStarted?.Invoke(
							_recognizer,
							new ManipulationStartedEventArgs(_deviceType, _currents.Center, cumulative, _contacts.onStart));
						// No needs to publish an update when we start the manipulation due to an additional pointer as cumulative will be empty.
						break;

					case ManipulationState.Starting when cumulative.IsSignificant(_startThresholds):
						_state = ManipulationState.Started;
						_contacts.onStart = _contacts.current;

						// Note: We first start with an empty delta, then invoke Update.
						//		 This is required to patch a common issue in applications that are using only the
						//		 ManipulationUpdated.Delta property to track the pointer (like the WCT GridSplitter).
						//		 UWP seems to do that only for Touch and Pen (i.e. the Delta is not empty on start with a mouse),
						//		 but there is no side effect to use the same behavior for all pointer types.

						UpdatePublishedState(cumulative);
						_recognizer.ManipulationStarted?.Invoke(
							_recognizer,
							new ManipulationStartedEventArgs(_deviceType, _origins.Center, ManipulationDelta.Empty, _contacts.onStart));
						_recognizer.ManipulationUpdated?.Invoke(
							_recognizer,
							new ManipulationUpdatedEventArgs(_deviceType, _currents.Center, cumulative, cumulative, ManipulationVelocities.Empty, isInertial: false, _contacts.onStart, _contacts.current));
						break;

					case ManipulationState.Started when IsDragManipulation:
						_recognizer.Dragging?.Invoke(
							_recognizer,
							new DraggingEventArgs(_currents.Pointer1, DraggingState.Continuing, _contacts.onStart));
						break;

					case ManipulationState.Started when pointerRemoved && ShouldStartInertia(velocities):
						_state = ManipulationState.Inertia;
						_inertia = new InertiaProcessor(this, cumulative, velocities);

						UpdatePublishedState(delta);
						_recognizer.ManipulationInertiaStarting?.Invoke(
							_recognizer,
							new ManipulationInertiaStartingEventArgs(_deviceType, _currents.Center, delta, cumulative, velocities, _contacts.onStart, _inertia));

						_inertia.Start();
						break;

					case ManipulationState.Started when pointerRemoved:
					// For now we complete the Manipulation as soon as a pointer was removed.
					// This is not the UWP behavior where for instance you can scale multiple times by releasing only one finger.
					// It's however the right behavior in case of drag conflicting with manipulation (which is not supported by Uno).
					case ManipulationState.Inertia when !_inertia!.IsRunning:
						Complete();
						break;

					case ManipulationState.Started when pointerAdded:
					case ManipulationState.Started when delta.IsSignificant(_deltaThresholds):
					case ManipulationState.Inertia: // No significant check for inertia, we prefer smooth animations!
						UpdatePublishedState(delta);
						_recognizer.ManipulationUpdated?.Invoke(
							_recognizer,
							new ManipulationUpdatedEventArgs(_deviceType, _currents.Center, delta, cumulative, velocities, _state == ManipulationState.Inertia, _contacts.onStart, _contacts.current));
						break;
				}
			}

			[Pure]
			private ManipulationDelta GetCumulative()
			{
				if (_inertia is { } inertia)
				{
					return inertia.GetCumulative();
				}

				var translateX = _isTranslateXEnabled ? _currents.Center.X - _origins.Center.X : 0;
				var translateY = _isTranslateYEnabled ? _currents.Center.Y - _origins.Center.Y : 0;
#if __MACOS__
				// correction for translateY being inverted (#4700)
				translateY *= -1;
#endif

				double rotate;
				float scale, expansion;
				if (_currents.HasPointer2)
				{
					rotate = _isRotateEnabled ? _currents.Angle - _origins.Angle : 0;
					scale = _isScaleEnabled ? _currents.Distance / _origins.Distance : 1;
					expansion = _isScaleEnabled ? _currents.Distance - _origins.Distance : 0;
				}
				else
				{
					rotate = 0;
					scale = 1;
					expansion = 0;
				}

				return new ManipulationDelta
				{
					Translation = new Point(translateX, translateY),
					Rotation = (float)MathEx.ToDegreeNormalized(rotate),
					Scale = scale,
					Expansion = expansion
				};
			}

			[Pure]
			private ManipulationDelta GetDelta(ManipulationDelta cumulative)
			{
				var deltaSum = _lastPublishedState.sumOfDelta;

				var translateX = _isTranslateXEnabled ? cumulative.Translation.X - deltaSum.Translation.X : 0;
				var translateY = _isTranslateYEnabled ? cumulative.Translation.Y - deltaSum.Translation.Y : 0;
				var rotate = _isRotateEnabled ? cumulative.Rotation - deltaSum.Rotation : 0;
				var scale = _isScaleEnabled ? cumulative.Scale / deltaSum.Scale : 1;
				var expansion = _isScaleEnabled ? cumulative.Expansion - deltaSum.Expansion : 0;

				return new ManipulationDelta
				{
					Translation = new Point(translateX, translateY),
					Rotation = (float)MathEx.NormalizeDegree(rotate),
					Scale = scale,
					Expansion = expansion
				};
			}

			[Pure]
			private ManipulationVelocities GetVelocities(ManipulationDelta delta)
			{
				var fromTime = _lastPublishedState.timestamp;
				var currentTime = _currents.Timestamp;

				var ms = (double)(currentTime - fromTime) / TimeSpan.TicksPerMillisecond;

				// With uno a single native event might produce multiple managed pointer events.
				// In that case we would get en empty velocities ... which is often not relevant!
				// When we detect that case, we prefer to replay the last known velocities.
				if (delta.IsEmpty || ms == 0)
				{
					return _lastRelevantVelocities;
				}

				var linearX = delta.Translation.X / ms;
				var linearY = delta.Translation.Y / ms;
				var angular = delta.Rotation / ms;
				var expansion = delta.Expansion / ms;

				var velocities = new ManipulationVelocities
				{
					Linear = new Point(linearX, linearY),
					Angular = (float)angular,
					Expansion = (float)expansion
				};

				if (velocities.IsAnyAbove(default))
				{
					_lastRelevantVelocities = velocities;
				}

				return _lastRelevantVelocities;
			}

			// This has to be invoked before any event being raised, it will update the internal that is used to compute delta and velocities.
			private void UpdatePublishedState(ManipulationDelta delta)
			{
				_lastPublishedState = (_lastPublishedState.sumOfDelta.Add(delta), _currents.Timestamp, _contacts.current);
			}

			/// <summary>
			/// Is this manipulation (a) valid to become a drag and (b) held for long enough to count as a drag?
			/// </summary>
			[Pure]
			internal bool IsHeldLongEnoughToDrag()
			{
				if (!_isDraggingEnable)
				{
					return false;
				}

				var down = _origins.Pointer1;
				var current = _currents.Pointer1; // For current to be current, this should be called after TryUpdate()
				var isInHoldPhase = current.Timestamp - down.Timestamp < DragWithTouchMinDelayTicks;
				return !isInHoldPhase;
			}

			// For pen and mouse this only means down -> * moves out of tap range;
			// For touch it means down -> * moves close to origin for DragUsingFingerMinDelayTicks -> * moves far from the origin 
			[Pure]
			private bool IsBeginningOfDragManipulation()
			{
				if (!_isDraggingEnable)
				{
					return false;
				}

				// Note: We use the TapRange and not the manipulation's start threshold as, for mouse and pen,
				//		 those thresholds are lower than a Tap (and actually only 1px), which does not math the UWP behavior.
				var down = _origins.Pointer1;
				var current = _currents.Pointer1;
				var isOutOfRange = Gesture.IsOutOfTapRange(down.Position, current.Position);

				switch (_deviceType)
				{
					case PointerDeviceType.Mouse:
					case PointerDeviceType.Pen:
						return isOutOfRange;

					default:
					case PointerDeviceType.Touch:
						// As for holding, here we rely on the fact that we get a lot of small moves due to the lack of precision
						// of the touch device (cf. Gesture.NeedsHoldingTimer).
						// This means that this method is expected to be invoked on each move (until manipulation starts)
						// in order to update the _isDraggingEnable state.

						var isInHoldPhase = current.Timestamp - down.Timestamp < DragWithTouchMinDelayTicks;
						if (isInHoldPhase && isOutOfRange)
						{
							// The pointer moved out of range while in the hold phase, so we completely disable the drag manipulation
							_isDraggingEnable = false;
							_recognizer.TryCancelHapticFeedbackTimer();
							return false;
						}
						else
						{
							// The drag should start only after the hold delay and if the pointer moved out of the range
							return !isInHoldPhase && isOutOfRange;
						}
				}
			}

			[Pure]
			private bool ShouldStartInertia(ManipulationVelocities velocities)
				=> _inertia is null
					&& !IsDragManipulation
					&& (_settings & GestureSettingsHelper.Inertia) != 0
					&& velocities.IsAnyAbove(_inertiaThresholds);

			internal struct Thresholds
			{
				public double TranslateX;
				public double TranslateY;
				public double Rotate; // Degrees
				public double Expansion;
			}

			// WARNING: This struct is ** MUTABLE **
			private struct Points
			{
				public PointerPoint Pointer1;
				private PointerPoint? _pointer2;

				public ulong Timestamp;
				public Point Center; // This is the center in ** absolute ** coordinates spaces (i.e. relative to the screen)
				public float Distance;
				public double Angle;

				public bool HasPointer2 => _pointer2 != null;

				public Points(PointerPoint point)
				{
					Pointer1 = point;
					_pointer2 = default;

					Timestamp = point.Timestamp;
					Center = point.RawPosition; // RawPosition => cf. Note in UpdateComputedValues().
					Distance = 0;
					Angle = 0;
				}

				public bool ContainsPointer(uint pointerId)
					=> Pointer1.PointerId == pointerId
					|| (HasPointer2 && _pointer2!.PointerId == pointerId);

				public void SetPointer2(PointerPoint point)
				{
					_pointer2 = point;
					UpdateComputedValues();
				}

				public bool TryUpdate(PointerPoint point)
				{
					if (Pointer1.PointerId == point.PointerId)
					{
						Pointer1 = point;
						Timestamp = point.Timestamp;

						UpdateComputedValues();

						return true;
					}
					else if (_pointer2 != null && _pointer2.PointerId == point.PointerId)
					{
						_pointer2 = point;
						Timestamp = point.Timestamp;

						UpdateComputedValues();

						return true;
					}
					else
					{
						return false;
					}
				}

				private void UpdateComputedValues()
				{
					// Note: Here we use the RawPosition in order to work in the ** absolute ** screen coordinates system
					//		 This is required to avoid to be impacted the any transform applied on the element,
					//		 and it's sufficient as values of the manipulation events are only values relative to the original touch point.

					if (_pointer2 == null)
					{
						Center = Pointer1.RawPosition;
						Distance = 0;
						Angle = 0;
					}
					else
					{
						var p1 = Pointer1.RawPosition;
						var p2 = _pointer2.RawPosition;

						Center = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
						Distance = Vector2.Distance(p1.ToVector(), p2.ToVector());
						Angle = Math.Atan2(p1.Y - p2.Y, p1.X - p2.X);
					}
				}

				public static implicit operator Points(PointerPoint pointer1)
					=> new Points(pointer1);
			}

			internal class InertiaProcessor : IDisposable
			{
				// TODO: We should somehow sync tick with frame rendering
				const double framePerSecond = 25;
				const double durationTicks = 1.5 * TimeSpan.TicksPerSecond;

				private readonly DispatcherQueueTimer _timer;
				private readonly Manipulation _owner;
				private readonly ManipulationDelta _initial;

				private readonly bool _isTranslateInertiaXEnabled;
				private readonly bool _isTranslateInertiaYEnabled;
				private readonly bool _isRotateInertiaEnabled;
				private readonly bool _isScaleInertiaEnabled;

				public double DesiredDisplacement;
				public double DesiredDisplacementDeceleration;
				public double DesiredRotation;
				public double DesiredRotationDeceleration;
				public double DesiredExpansion;
				public double DesiredExpansionDeceleration;

				public InertiaProcessor(Manipulation owner, ManipulationDelta cumulative, ManipulationVelocities velocities)
				{
					_owner = owner;
					_initial = cumulative;

					_isTranslateInertiaXEnabled = _owner._isTranslateXEnabled && _owner._settings.HasFlag(Input.GestureSettings.ManipulationTranslateInertia);
					_isTranslateInertiaYEnabled = _owner._isTranslateYEnabled && _owner._settings.HasFlag(Input.GestureSettings.ManipulationTranslateInertia);
					_isRotateInertiaEnabled = _owner._isRotateEnabled && _owner._settings.HasFlag(Input.GestureSettings.ManipulationRotateInertia);
					_isScaleInertiaEnabled = _owner._isScaleEnabled && _owner._settings.HasFlag(Input.GestureSettings.ManipulationScaleInertia);

					_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
					_timer.Interval = TimeSpan.FromMilliseconds(1000d / framePerSecond);
					_timer.IsRepeating = true;
					_timer.Tick += OnTick;

					// TODO
					DesiredDisplacement = _isTranslateInertiaXEnabled || _isTranslateInertiaYEnabled ? 300 : 0;
					DesiredRotation = _isRotateInertiaEnabled ? 60 : 0;
					DesiredExpansion = _isScaleInertiaEnabled ? 200 : 0;
				}

				public bool IsRunning => _timer.IsRunning;

				public void Start()
					=> _timer.Start();

				public ManipulationDelta GetCumulative()
				{
					var progress = 1 - Math.Pow(1 - GetNormalizedTime(), 4); // Source: https://easings.net/#easeOutQuart

					var translateX = _isTranslateInertiaXEnabled ? _initial.Translation.X + progress * DesiredDisplacement : 0;
					var translateY = _isTranslateInertiaYEnabled ? _initial.Translation.Y + progress * DesiredDisplacement : 0;
					var rotate = _isRotateInertiaEnabled ? _initial.Rotation + progress * DesiredRotation : 0;
					var expansion = _isScaleInertiaEnabled ? _initial.Expansion + progress * DesiredExpansion : 0;

					var scale = (_owner._origins.Distance + expansion) / _owner._origins.Distance;

					return new ManipulationDelta
					{
						Translation = new Point(translateX, translateY),
						Rotation = (float)MathEx.NormalizeDegree(rotate),
						Scale = (float)scale,
						Expansion = (float)expansion
					};
				}

				private double GetNormalizedTime()
				{
					var elapsed = _timer.LastTickElapsed;
					var normalizedTime = elapsed.Ticks / durationTicks;

					return normalizedTime;
				}

				private void OnTick(DispatcherQueueTimer sender, object args)
				{
					_owner.NotifyUpdate();

					if (GetNormalizedTime() >= 1)
					{
						_timer.Stop();
						_owner.NotifyUpdate();
					}
				}

				/// <inheritdoc />
				public void Dispose()
					=> _timer.Stop();
			}
		}
	}
}
