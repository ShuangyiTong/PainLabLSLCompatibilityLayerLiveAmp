using System;
using System.IO;
using System.IO.Ports;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using LSL;

namespace PainLabDeviceLSLCompatialeLayer
{
    public class ChannelDataJsonConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var data_streams = ((LSLDataFrame)value).data_streams;
            writer.WriteStartObject();
            for (int i = 0; i < data_streams[0].Length; i++)
            {
                writer.WritePropertyName("Ch" + Convert.ToString(i + 1));
                writer.WriteStartArray();
                for (int j = 0; j < data_streams.Length; j++)
                {
                    writer.WriteValue(data_streams[j][i]);
                }
                writer.WriteEndArray();
            }
            writer.WritePropertyName("last_trigger_on_client");
            writer.WriteValue(((LSLDataFrame)value).last_trigger_ts);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException("Unnecessary because CanRead is false. The type will skip the converter.");
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(DateTime);
        }

    }

    [JsonConverter(typeof(ChannelDataJsonConverter))]
    class LSLDataFrame
    {
        public float[][] data_streams;
        public long last_trigger_ts;

        public LSLDataFrame(float[][] sample, long last_ts)
        {
            // will be transposed in json converter
            data_streams = sample;
            last_trigger_ts = last_ts;
        }
    }

    [Serializable]
    class LiveAmpControlFrame
    {
        public int trigger_channel = 0;
        public long ApplyControlData(SerialPort sp)
        {
            Byte[] data = { (Byte)trigger_channel };

            sp.Write(data, 0, 1);
            Thread.Sleep(10);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            data[0] = 0x01;
            sp.Write(data, 0, 1);
            Thread.Sleep(10);

            data[0] = 0x00; 
            sp.Write(data, 0, 1); 
            Thread.Sleep(10);

            return now.ToUnixTimeMilliseconds();
        }
    }

    class PainlabLSLCompatiblilityProtocol : PainlabProtocol
    {
        static string descriptorPath = "Resources/device-descriptor.json";
        public StreamInfo info;
        SerialPort sp;
        long last_ts = -1;
        protected override void RegisterWithDescriptor()
        {
            string descriptorString = File.ReadAllText(descriptorPath);
            JObject Jdescriptor = JObject.Parse(descriptorString);

            JObject Jchannel_names = new JObject();
            JObject Jvisual_settings = new JObject();
            foreach (int value in Enumerable.Range(1, info.channel_count()))
            {
                Jchannel_names.Add("Ch" + Convert.ToString(value), "float");
                Jvisual_settings.Add("Ch" + Convert.ToString(value), "static");
            }
            Jchannel_names.Add("last_trigger_on_client", "int");

            Jdescriptor.Add("data_to_report", Jchannel_names);
            Jdescriptor.Add("lsl_descriptor", info.as_xml());
            Jdescriptor.Add("visual_report", Jvisual_settings);

            SendString(Jdescriptor.ToString());

            return;
        }

        protected override void ApplyControlData()
        {
            LiveAmpControlFrame controlFrame
                    = JsonConvert.DeserializeObject<LiveAmpControlFrame>
                      (Encoding.UTF8.GetString(_controlBuffer, 0, (int)_numControlBytes));

            last_ts = controlFrame.ApplyControlData(sp);
        }

        public void ControlApplicationThread()
        {
            while (true)
            {
                _waitOnControlSem.WaitOne();
                HandlingControlData();
            }
        }

        public byte[] PrepareDataFrameBytes(float[][] sample)
        {
            LSLDataFrame dataFrame = new LSLDataFrame(sample, last_ts);
            byte[] byteData = StringToBytes(JsonConvert.SerializeObject(dataFrame, Formatting.None));

            return byteData;
        }

        public void setupTriggerSerialPort()
        {
            sp = new SerialPort("COM7");
            sp.Open();
            sp.ReadTimeout = 5000;

            // Attach the data received event handler
            sp.DataReceived += TriggerBox_DataReceived;
        }

        static void TriggerBox_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort sp = (SerialPort)sender;
            // get all available bytes from the input buffer
            while (sp.BytesToRead > 0)
            {
                Console.WriteLine(sp.ReadByte());
            }
        }
    }
    class Program
    {
        static string networkConfigPath = "Resources/network-config.json";
        static int subFramePerFrame = 20;
        static void Main(string[] args)
        {
            PainlabLSLCompatiblilityProtocol protocol = new PainlabLSLCompatiblilityProtocol();
            string networkJsonString = File.ReadAllText(networkConfigPath);
            NetworkConfig netConf = JsonConvert.DeserializeObject<NetworkConfig>(networkJsonString);


            // wait until an EEG stream shows up
            StreamInfo[] results = LSL.LSL.resolve_stream("type", "EEG");

            // open an inlet and print some interesting info about the stream (meta-data, etc.)
            StreamInlet inlet = new StreamInlet(results[0]);
            results.DisposeArray();
            System.Console.Write(inlet.info().as_xml());

            protocol.info = inlet.info();

            protocol.Init(netConf, waitOnControl: true);
            protocol.setupTriggerSerialPort();

            Thread controlThread = new Thread(new ThreadStart(protocol.ControlApplicationThread));
            controlThread.Start();

            float[][] sample = new float[subFramePerFrame][];
            for (int i = 0; i < subFramePerFrame; i++)
            {
                sample[i] = new float[inlet.info().channel_count()];
            }
            int subframe_counter = 0;
            // read samples
            while (true)
            {
                inlet.pull_sample(sample[subframe_counter]);
                subframe_counter++;
                if (subframe_counter >= subFramePerFrame)
                {
                    protocol.UpdateFrameData(protocol.PrepareDataFrameBytes(sample));
                    subframe_counter = 0;
                }
                
            }
        }
    }
}
