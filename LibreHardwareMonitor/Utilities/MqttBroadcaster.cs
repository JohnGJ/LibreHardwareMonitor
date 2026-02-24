// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.UI;
using MQTTnet;
using MQTTnet.Client;

namespace LibreHardwareMonitor.Utilities;

public class MqttBroadcaster
{
    private readonly PersistentSettings _settings;

    private IMqttClient? _mqttClient;

    private string _mqttHost;
    private int _mqttPort;
    private bool _mqttUseTls;
    private string _mqttUsername;
    private string _mqttPassword;
    private string _mqttBaseTopic;
    private string _mqttClientId;
    private int _mqttQos;
    private bool _mqttRetain;
    private bool _mqttPublishJson;

    public MqttBroadcaster(PersistentSettings settings)
    {
        _settings = settings;

        ReloadSettings();
    }

    public void Start()
    {
        ReloadSettings();
        _mqttClient ??= new MqttFactory().CreateMqttClient();

        // Placeholder for connect/reconnect logic.
    }

    public void Stop()
    {
        // Placeholder for disconnect/cleanup logic.
    }

    public void PublishSelectedSensors(Node root, DateTime timestamp)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));

        ReloadSettings();

        List<ISensor> selectedSensors = new();
        CollectSelectedSensors(root, selectedSensors);

        _ = timestamp;
        _ = selectedSensors;

        // Placeholder for publish logic.
    }

    private void ReloadSettings()
    {
        _mqttHost = _settings.GetValue("mqttHost", "localhost");
        _mqttPort = _settings.GetValue("mqttPort", 1883);
        _mqttUseTls = _settings.GetValue("mqttUseTls", false);
        _mqttUsername = _settings.GetValue("mqttUsername", "");
        _mqttPassword = _settings.GetValue("mqttPassword", "");
        _mqttBaseTopic = _settings.GetValue("mqttBaseTopic", "librehardwaremonitor");
        _mqttClientId = _settings.GetValue("mqttClientId", "");
        _mqttQos = _settings.GetValue("mqttQos", 0);
        _mqttRetain = _settings.GetValue("mqttRetain", true);
        _mqttPublishJson = _settings.GetValue("mqttPublishJson", true);
    }

    private void CollectSelectedSensors(Node currentNode, ICollection<ISensor> selectedSensors)
    {
        if (currentNode is SensorNode sensorNode)
        {
            string settingName = new Identifier(sensorNode.Sensor.Identifier, "mqtt").ToString();
            bool isSelectedForMqtt = _settings.GetValue(settingName, false);
            if (isSelectedForMqtt)
                selectedSensors.Add(sensorNode.Sensor);
        }

        foreach (Node childNode in currentNode.Nodes)
            CollectSelectedSensors(childNode, selectedSensors);
    }
}
