using System;
using System.IO;
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
            for (int i = 0; i < data_streams.Length; i++)
            {
                writer.WritePropertyName("Ch" + Convert.ToString(i + 1));
                writer.WriteStartArray();
                for (int j = 0; j < data_streams[i].Length; j++)
                {
                    writer.WriteValue(data_streams[i][j]);
                }
                writer.WriteEndArray();
            }
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
        public double[][] data_streams;

        public LSLDataFrame(float[][] sample)
        {
            // Transpose operation
            data_streams = new double[sample[0].Length][];
            for (int i = 0; i < sample[0].Length; i++)
            {
                data_streams[i] = new double[sample.Length];
                for (int j = 0; j < sample.Length; j++)
                {
                    data_streams[i][j] = (double)sample[j][i];
                }
            }
        }
    }

    class PainlabLSLCompatiblilityProtocol : PainlabProtocol
    {
        static string descriptorPath = "Resources/device-descriptor.json";
        public StreamInfo info;
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

            Jdescriptor.Add("data_to_report", Jchannel_names);
            Jdescriptor.Add("lsl_descriptor", info.as_xml());
            Jdescriptor.Add("visual_report", Jvisual_settings);
            
            SendString(Jdescriptor.ToString());

            return;
        }
        public byte[] PrepareDataFrameBytes(float[][] sample)
        {
            LSLDataFrame dataFrame = new LSLDataFrame(sample);
            byte[] byteData = StringToBytes(JsonConvert.SerializeObject(dataFrame, Formatting.None));

            return byteData;
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

            protocol.Init(netConf);

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
