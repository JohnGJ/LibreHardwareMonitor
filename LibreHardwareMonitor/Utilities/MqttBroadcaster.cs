// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.UI;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace LibreHardwareMonitor.Utilities;

public class MqttBroadcaster
{
    private readonly PersistentSettings _settings;

    private IMqttClient? _mqttClient;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private readonly object _lifecycleLock = new();

    private readonly ConcurrentDictionary<string, float> _lastPublishedValues = new();
    private readonly object _publishLock = new();
    private Task _publishTask = Task.CompletedTask;

    private string _mqttHost = "localhost";
    private int _mqttPort = 1883;
    private bool _mqttUseTls;
    private string _mqttUsername = string.Empty;
    private string _mqttPassword = string.Empty;
    private string _mqttBaseTopic = "librehardwaremonitor";
    private string _mqttClientId = string.Empty;
    private int _mqttQos;
    private bool _mqttRetain = true;
    private bool _mqttPublishJson = true;

    public MqttBroadcaster(PersistentSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ReloadSettings();

            if (_mqttClient == null)
            {
                _mqttClient = new MqttFactory().CreateMqttClient();
                _mqttClient.DisconnectedAsync += OnMqttClientDisconnectedAsync;
            }

            if (_reconnectTask is { IsCompleted: false })
                return;

            _reconnectCts = new CancellationTokenSource();
            _reconnectTask = Task.Run(() => RunReconnectLoopAsync(_reconnectCts.Token));
        }
    }

    public void Stop()
    {
        IMqttClient? mqttClient;
        CancellationTokenSource? reconnectCts;
        Task? reconnectTask;

        lock (_lifecycleLock)
        {
            mqttClient = _mqttClient;
            reconnectCts = _reconnectCts;
            reconnectTask = _reconnectTask;

            _mqttClient = null;
            _reconnectCts = null;
            _reconnectTask = null;
        }

        try
        {
            reconnectCts?.Cancel();

            if (reconnectTask != null)
            {
                try
                {
                    reconnectTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                {
                }
            }

            if (mqttClient != null && mqttClient.IsConnected)
                mqttClient.DisconnectAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MQTT stop failed: {ex.Message}");
        }
        finally
        {
            if (mqttClient != null)
                mqttClient.DisconnectedAsync -= OnMqttClientDisconnectedAsync;

            reconnectCts?.Dispose();
        }
    }

    public void PublishSelectedSensors(Node root, DateTime timestamp)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));

        IMqttClient? mqttClient = _mqttClient;
        if (mqttClient == null || !mqttClient.IsConnected)
            return;

        List<ISensor> selectedSensors = new();
        CollectSelectedSensors(root, selectedSensors);

        if (selectedSensors.Count == 0)
            return;

        QueuePublish(selectedSensors, timestamp);
    }

    private void QueuePublish(IReadOnlyCollection<ISensor> sensors, DateTime timestamp)
    {
        lock (_publishLock)
        {
            _publishTask = _publishTask
                .ContinueWith(
                    _ => PublishSensorsAsync(sensors, timestamp),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task PublishSensorsAsync(IReadOnlyCollection<ISensor> sensors, DateTime timestamp)
    {
        try
        {
            IMqttClient? mqttClient = _mqttClient;
            if (mqttClient == null || !mqttClient.IsConnected)
                return;

            foreach (ISensor sensor in sensors)
            {
                float? sensorValue = sensor.Value;
                if (!sensorValue.HasValue)
                    continue;

                string key = sensor.Identifier.ToString();
                if (_lastPublishedValues.TryGetValue(key, out float previousValue) && previousValue.Equals(sensorValue.Value))
                    continue;

                string topic = BuildSensorTopic(sensor);
                string payload = BuildPayload(sensor, sensorValue.Value, timestamp);

                MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(ToQosLevel(_mqttQos))
                    .WithRetainFlag(_mqttRetain)
                    .Build();

                await mqttClient.PublishAsync(message, CancellationToken.None);
                _lastPublishedValues[key] = sensorValue.Value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MQTT publish failed: {ex.Message}");
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                IMqttClient? mqttClient = _mqttClient;
                if (mqttClient == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }

                if (mqttClient.IsConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }

                await ConnectAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MQTT reconnect loop failed: {ex.Message}");
        }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        int[] backoffSeconds = { 1, 2, 5, 10, 30, 60 };
        DateTime? nextLogTimeUtc = null;

        while (!token.IsCancellationRequested)
        {
            IMqttClient? mqttClient = _mqttClient;
            if (mqttClient == null)
                return;

            foreach (int backoff in backoffSeconds)
            {
                token.ThrowIfCancellationRequested();

                mqttClient = _mqttClient;
                if (mqttClient == null)
                    return;

                if (mqttClient.IsConnected)
                    return;

                ReloadSettings();
                MqttClientOptions options = BuildClientOptions();

                try
                {
                    await mqttClient.ConnectAsync(options, token);

                    nextLogTimeUtc = null;
                    await PublishConnectionStatusAsync(mqttClient, "online", token);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DateTime utcNow = DateTime.UtcNow;
                    if (!nextLogTimeUtc.HasValue || utcNow >= nextLogTimeUtc.Value)
                    {
                        Debug.WriteLine($"MQTT connect failed, retrying in {backoff}s: {ex.Message}");
                        nextLogTimeUtc = utcNow.AddSeconds(backoff);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(backoff), token);
                }
            }
        }
    }

    private MqttClientOptions BuildClientOptions()
    {
        string machineSegment = SanitizeTopicSegment(Environment.MachineName);
        string statusTopic = BuildStatusTopic(machineSegment);

        MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqttHost, _mqttPort)
            .WithClientId(GetClientId(machineSegment));

        if (!string.IsNullOrWhiteSpace(_mqttUsername))
            builder = builder.WithCredentials(_mqttUsername, _mqttPassword);

        if (_mqttUseTls)
            builder = builder.WithTls();

        MqttApplicationMessage lwtMessage = new MqttApplicationMessageBuilder()
            .WithTopic(statusTopic)
            .WithPayload("offline")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        builder = builder.WithWillMessage(lwtMessage);

        return builder.Build();
    }

    private Task OnMqttClientDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        Debug.WriteLine($"MQTT disconnected: {args.ReasonString}");
        return Task.CompletedTask;
    }

    private async Task PublishConnectionStatusAsync(IMqttClient mqttClient, string status, CancellationToken token)
    {
        string statusTopic = BuildStatusTopic(SanitizeTopicSegment(Environment.MachineName));

        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(statusTopic)
            .WithPayload(status)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await mqttClient.PublishAsync(message, token);
    }

    private string BuildSensorTopic(ISensor sensor)
    {
        string baseTopic = SanitizeTopicSegment(string.IsNullOrWhiteSpace(_mqttBaseTopic) ? "librehardwaremonitor" : _mqttBaseTopic);
        string machine = SanitizeTopicSegment(Environment.MachineName);
        string hardwareIdentifier = sensor.Hardware.Identifier.ToString();
        string sensorIdentifier = sensor.Identifier.ToString();
        string relativeSensorId = sensorIdentifier.StartsWith(hardwareIdentifier, StringComparison.Ordinal)
            ? sensorIdentifier.Substring(hardwareIdentifier.Length).Trim('/', '\\')
            : sensorIdentifier;

        string hardwareId = SanitizeTopicSegment(hardwareIdentifier);
        string sensorId = SanitizeTopicSegment(relativeSensorId);

        return JoinTopic(baseTopic, machine, hardwareId, sensorId);
    }

    private string BuildStatusTopic(string machineSegment)
    {
        string baseTopic = SanitizeTopicSegment(string.IsNullOrWhiteSpace(_mqttBaseTopic) ? "librehardwaremonitor" : _mqttBaseTopic);
        return JoinTopic(baseTopic, machineSegment, "status");
    }

    private static string JoinTopic(params string[] segments)
    {
        return string.Join("/", segments);
    }

    private string BuildPayload(ISensor sensor, float value, DateTime timestamp)
    {
        if (!_mqttPublishJson)
            return value.ToString(CultureInfo.InvariantCulture);

        var payload = new
        {
            name = sensor.Name,
            value,
            unit = GetSensorUnit(sensor.SensorType),
            sensorType = sensor.SensorType.ToString(),
            hardwareName = sensor.Hardware.Name,
            hardwareType = sensor.Hardware.HardwareType.ToString(),
            identifier = sensor.Identifier.ToString(),
            timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string GetSensorUnit(SensorType sensorType)
    {
        return sensorType switch
        {
            SensorType.Voltage => "V",
            SensorType.Current => "A",
            SensorType.Power => "W",
            SensorType.Clock => "MHz",
            SensorType.Temperature => "°C",
            SensorType.Load => "%",
            SensorType.Frequency => "Hz",
            SensorType.Fan => "RPM",
            SensorType.Flow => "L/h",
            SensorType.Control => "%",
            SensorType.Level => "%",
            SensorType.Factor => "1",
            SensorType.Data => "GB",
            SensorType.SmallData => "MB",
            SensorType.Throughput => "B/s",
            SensorType.TimeSpan => "s",
            SensorType.Timing => "ns",
            SensorType.Energy => "mWh",
            SensorType.Noise => "dBA",
            SensorType.Conductivity => "µS/cm",
            SensorType.Humidity => "%",
            _ => string.Empty
        };
    }

    private static MqttQualityOfServiceLevel ToQosLevel(int mqttQos)
    {
        return mqttQos switch
        {
            1 => MqttQualityOfServiceLevel.AtLeastOnce,
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => MqttQualityOfServiceLevel.AtMostOnce
        };
    }

    private string GetClientId(string machineSegment)
    {
        string configuredClientId = SanitizeTopicSegment(_mqttClientId);
        if (!string.IsNullOrWhiteSpace(configuredClientId))
            return configuredClientId;

        return $"librehardwaremonitor-{machineSegment}";
    }

    private static string SanitizeTopicSegment(string? input)
    {
        string value = (input ?? string.Empty).Trim();
        value = value.Trim('/', '\\');

        if (value.Length == 0)
            return "unknown";

        StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            sb.Append(c switch
            {
                '/' or '\\' or '#' or '+' or ' ' => '_',
                _ => c
            });
        }

        return sb.ToString();
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
