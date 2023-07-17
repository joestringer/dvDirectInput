using BepInEx;
using BepInEx.Configuration;
using DV.HUD;
using DV.Simulation.Cars;
using SharpDX.DirectInput;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static DV.HUD.InteriorControlsManager;
using static dvDirectInput.Plugin;
using static System.Collections.Specialized.BitVector32;

namespace dvDirectInput
{
	[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
	public class Plugin : BaseUnityPlugin
	{
		public class ConfigControls
		{
			public ConfigEntry<bool> Enabled;
			public ConfigEntry<int> DeviceId;
			public ConfigEntry<JoystickOffset> DeviceOffset;
		}

		// Config - Debug
		private ConfigEntry<bool> configEnableRecentInputGUI;

		// Config - Controls
		public ConfigControls configControlsThrottle = new ConfigControls();
		public ConfigControls configControlsTrainBrake = new ConfigControls();
		public ConfigControls configControlsIndependentBrake = new ConfigControls();
		public ConfigControls configControlsDynamicBrake = new ConfigControls();
		public ConfigControls configControlsReverser = new ConfigControls();

		public struct Input
		{
			public int JoystickId { get; set; }
			public int Value { get; set; }
			public float NormalisedValue => (float)Value / UInt16.MaxValue;
			public int Timestamp { get; set; }
			public JoystickOffset Offset;

			public override string ToString()
			{
				return string.Format(CultureInfo.InvariantCulture, "ID: {0}, Offset: {1}, Value: {2}, Timestamp {3}", JoystickId, Offset, Value, Timestamp);
			}
		}

		private List<Joystick> joysticks = new List<Joystick>();
		private Queue<Input> inputQueue = new Queue<Input>();

		private List<Queue<JoystickUpdate>> joysticksRecentInputs = new List<Queue<JoystickUpdate>>();

		List<KeyValuePair<ControlType, ConfigControls>> mapControl2Input = new List<KeyValuePair<ControlType, ConfigControls>>(); 


		// Loading Mod
		private void Awake()
		{
			// Plugin startup logic
			Logger.LogInfo($"Plugin [{PluginInfo.PLUGIN_GUID}|{PluginInfo.PLUGIN_NAME}|{PluginInfo.PLUGIN_VERSION}] is loaded!");

			// Config
			BindAllConfigs();
			MapControls2Inputs();

			// Initialise all Direct Input game controllers as joysticks
			// We may want to run this in the main logic in case of devices attached on the fly
			// Could be more complicated than it sounds
			var directInput = new DirectInput();
			var devices = directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AllDevices);
			foreach (var device in devices)
			{
				Joystick joystick = new Joystick(directInput, device.InstanceGuid);
				Logger.LogInfo($"DirectInput found device: {joystick.Information.ProductName}, {joystick.Properties.JoystickId}");

				// Set the max number of entries the joystick will return from a poll event via GetBufferedData()
				joystick.Properties.BufferSize = 128;

				// Open the Joystick and add it to a list
				joystick.Acquire();
				joysticks.Add(joystick);

				joysticksRecentInputs.Add(new Queue<JoystickUpdate>());
			}
		}

		// Every Frame
		void Update()
		{
			int currentTimestamp = 0;

			// Grab inputs for all controllers
			foreach (var joystick in joysticks.Select((val, idx) => new { idx, val }))
			{
				joystick.val.Poll();
				foreach (var data in joystick.val.GetBufferedData())
				{
					//Logger.LogInfo($"Device {joystick.val.Properties.JoystickId}: {data}");
					// Chuck all the inputs on a queue
					var input = new Input() { JoystickId = joystick.val.Properties.JoystickId, Offset = data.Offset, Value = data.Value, Timestamp = data.Timestamp };
					inputQueue.Enqueue(input);

					// GUI Logic - Copy of inputs
					if (configEnableRecentInputGUI.Value)
					{
						joysticksRecentInputs[joystick.idx].Enqueue(data);
						currentTimestamp = data.Timestamp;
					}
				}
				// GUI Logic - Remove any inputs if they have been displayed for a suitable period
				if (configEnableRecentInputGUI.Value)
				{
					if (joysticksRecentInputs[joystick.idx].Count > 0)
					{
						while (currentTimestamp - joysticksRecentInputs[joystick.idx].Peek().Timestamp > 1000)
						{
							joysticksRecentInputs[joystick.idx].Dequeue();
							if (joysticksRecentInputs[joystick.idx].Count == 0)
								break;
						}
					}
				}
			}

			// Main Logic
			while (inputQueue.Count > 0)
			{
				// Eat up all the queue items
				var input = inputQueue.Dequeue();

				// Dont bother doing anything if we arent in a loco
				if (!PlayerManager.Car?.IsLoco ?? true)
				{
					// Probbaly not going to get in a loco this game update so just clear the queue
					inputQueue.Clear();
					break;
				}
					
				// Assign Inputs
				foreach(var mapping in mapControl2Input)
				{
					// We should probably do a lookup for the inputs against the mappings instead of iterating
					if (mapping.Value.Enabled.Value && input.JoystickId == mapping.Value.DeviceId.Value && input.Offset == mapping.Value.DeviceOffset.Value)
					{
						PlayerManager.Car?.GetComponent<SimController>()?.controlsOverrider?.GetControl(mapping.Key)?.Set(input.NormalisedValue);
						break;
					}						
				}				
			}
		}

		// Every GUI event (possibly multiple times per frame)
		void OnGUI()
		{
			if (configEnableRecentInputGUI.Value)
			{
				// Show all the recognised game controlers and inputs to the user
				foreach (var joystick in joysticks.Select((val, idx) => new { idx, val }))
				{
					// Gets unique sorted input names from the recent input list
					var offsetList = new SortedSet<JoystickOffset>(joysticksRecentInputs[joystick.idx].Select(val => val.Offset).ToList().Distinct());

					// Just do a bunch of GUI stuff
					GUIStyle style = new GUIStyle();
					style.alignment = TextAnchor.MiddleLeft;
					style.stretchWidth = false;
					style.normal.textColor = Color.white;
					style.normal.background = Texture2D.grayTexture;

					GUILayout.BeginHorizontal(style);

					GUILayout.Label("Device Name: ", style);
					GUILayout.Label($"[{joystick.val.Information.ProductName}] ", style);

					style.normal.textColor = Color.gray;
					style.normal.background = Texture2D.whiteTexture;
					GUILayout.Label("Joystick ID:", style);
					GUILayout.Label($"[{joystick.val.Properties.JoystickId}] ", style);

					style.normal.textColor = Color.white;
					style.normal.background = Texture2D.grayTexture;
					GUILayout.Label(" Inputs: ", style);
					GUILayout.Label($"{String.Join(", ", offsetList)}", style);

					GUILayout.EndHorizontal();
				}
			}
		}
		// Unloading Mod
		void OnDestroy()
		{
			// Probably the right way to do this
			foreach (var joystick in joysticks)
			{
				joystick.Dispose();
			}

			// If the mod was reloaded at runtime we would want empty lists (is this done automatically?)
			joysticks.Clear();
			inputQueue.Clear();
			joysticksRecentInputs.Clear();
		}

		private void BindControlsConfigs(String section, ConfigControls config)
		{
			config.Enabled = Config.Bind($"Controls - {section}",
				"Enable",
				false,
				"Enables this input");

			config.DeviceId = Config.Bind($"Controls - {section}",
				"Input Device ID",
				0,
				"ID of input device provided by GUI");

			config.DeviceOffset = Config.Bind($"Controls - {section}",
				"Input Device Offset",
				JoystickOffset.X,
				"Input device offset axis/button provided by GUI");
		}

		private void BindAllConfigs()
		{
			// GUI
			configEnableRecentInputGUI = Config.Bind("Debug - GUI",
				"Enable",
				true,
				"Enable/Disable displaying recent inputs. Use this to identify the inputs for configuring the controls");

			//Controls
			BindControlsConfigs("Throttle", configControlsThrottle);
			BindControlsConfigs("Train Brake", configControlsTrainBrake);
			BindControlsConfigs("Independent Brake", configControlsIndependentBrake);
			BindControlsConfigs("Dynamic Brake", configControlsDynamicBrake);
			BindControlsConfigs("Reverser", configControlsReverser);
		}

		private void MapControls2Inputs()
		{
			// Map inputs
			// Todo MORE INPUTS
			// DirectInput exposes buttons so these should be added for other loco functions that havent been implemented here
			// These could include flipping breakers, starter, horn, sander etc.
			// Additionally those controllers with multi state dials/switcher/levers could use them for the reverser (3 state), lights (5 state?) etc
			mapControl2Input.Add(new KeyValuePair<ControlType, ConfigControls>(ControlType.Throttle, configControlsThrottle));
			mapControl2Input.Add(new KeyValuePair<ControlType, ConfigControls>(ControlType.TrainBrake, configControlsTrainBrake));
			mapControl2Input.Add(new KeyValuePair<ControlType, ConfigControls>(ControlType.IndBrake, configControlsIndependentBrake));
			mapControl2Input.Add(new KeyValuePair<ControlType, ConfigControls>(ControlType.DynamicBrake, configControlsDynamicBrake));
			mapControl2Input.Add(new KeyValuePair<ControlType, ConfigControls>(ControlType.Reverser, configControlsReverser));
			//mapControl2Input.Add(new KeyValuePair<ControlType, ConfigControls>(ControlType.Handbrake, ));

			//        Handbrake,
			//        Sander,
			//        Horn,
			//        HeadlightsFront,
			//        HeadlightsRear,
			//        StarterFuse,
			//        ElectricsFuse,
			//        TractionMotorFuse,
			//        StarterControl,
			//        CabLight,
			//        Wipers,
			//        FuelCutoff,
			//        ReleaseCyl,
			//        IndHeadlightsTypeFront,
			//        IndHeadlights1Front,
			//        IndHeadlights2Front,
			//        IndHeadlightsTypeRear,
			//        IndHeadlights1Rear,
			//        IndHeadlights2Rear,
			//        IndWipers1,
			//        IndWipers2,
			//        IndCabLight,
			//        IndDashLight,
			//        GearboxA,
			//        GearboxB,
			//        CylCock,
			//        Injector,
			//        Firedoor,
			//        Blower,
			//        Damper,
			//        Blowdown,
			//        CoalDump,
			//        Dynamo,
			//        AirPump,
			//        Lubricator,
			//        Bell
		}
	}

}
