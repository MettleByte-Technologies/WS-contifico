using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Xml.Linq;
using Newtonsoft.Json;
using OfficeOpenXml;
using DService.Models;
using System.Globalization;
using System.Configuration;

namespace DService
{
    public partial class Service1 : ServiceBase
    {
        Timer timer = new Timer(); // name space(using System.Timers;)
        private string folderPath = ConfigurationManager.AppSettings["SourcefolderPath"];
        private string processedFolderPath = ConfigurationManager.AppSettings["TargetfolderPath"];
        private readonly string apiKey = ConfigurationManager.AppSettings["apiKey"]; //"FrguR1kDpFHaXHLQwplZ2CwTX3p8p9XHVTnukL98V5U";
        private readonly string apiToken = ConfigurationManager.AppSettings["apiToken"]; //"dce704ae-189e-4545-bea3-257d9249a594";
        private readonly string apiUrl = ConfigurationManager.AppSettings["apiUrl"]; //"https://api.contifico.com/sistema/api/v1/documento/";


        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            WriteToFile("Service is started at " + DateTime.Now);
            timer.Elapsed += new ElapsedEventHandler(OnElapsedTime);
            timer.Interval = 5000; //number in milisecinds
            timer.Enabled = true;
        }

        protected override void OnStop()
        {
            WriteToFile("Service is stopped at " + DateTime.Now);
        }

        private void OnElapsedTime(object source, ElapsedEventArgs e)
        {
            WriteToFile("Checking for new Excel files at \n" + DateTime.Now);
            ProcessExcelFiles();
        }

        private void ProcessExcelFiles()
        {
            try
            {
                WriteToFile("Checking files at:" + folderPath);
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
                if (!Directory.Exists(processedFolderPath)) Directory.CreateDirectory(processedFolderPath);

                string[] files = Directory.GetFiles(folderPath, "*.xlsx");

                WriteToFile("files length:" + files.Length);

                if (files.Length == 0)
                {
                    WriteToFile("No files found in the source folder.");
                    return;
                }

                List<string> missingFilesLog = new List<string>();

                foreach (var file in files)
                {
                    WriteToFile($"Processing file: {file}");
                    // Read Excel data
                    string fetcha;
                    List<Detalle> detalles = ReadExcelData(file, out fetcha);
                    List<Cliente> pedidos = ReadExcelDataPedido(file);
                    // Process data if successfully extracted
                    if (detalles.Count > 0 && pedidos.Count > 0)
                    {
                        CreateDocumentAsync(detalles, pedidos, file, file,fetcha);
                    }
                    else
                    {
                        WriteToFile($"Skipping file {file}: Data extraction failed.");
                    }
                }
                // :fire: NEW: Display missing file report at the end
                if (missingFilesLog.Count > 0)
                {
                    WriteToFile("\nMissing Files Report:");
                    missingFilesLog.ForEach(WriteToFile);
                }
                else
                {
                    WriteToFile("All required file pairs are present.");
                }
            }
            catch (Exception ex)
            {
                WriteToFile("Error in processing files: " + ex.Message);
            }
        }

        private List<Detalle> ReadExcelData(string filePath,out string fecha)
        {
            var detalles = new List<Detalle>();
            fecha = ""; // Initialize fecha

            try
            {
                // ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
                ExcelPackage.License.SetNonCommercialPersonal("My Name");
                using (var package = new ExcelPackage(new FileInfo(filePath)))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    int rowCount = worksheet.Dimension?.Rows ?? 0;
                    int colCount = worksheet.Dimension?.Columns ?? 0;

                    if (rowCount == 0 || colCount == 0) return detalles;

                    var headers = new Dictionary<string, int>();
                    for (int col = 1; col <= colCount; col++)
                    {
                        string header = worksheet.Cells[1, col].Text.Trim().ToLower();
                        if (!string.IsNullOrEmpty(header))
                            headers[header] = col;
                    }

                    // ✅ Safe retrieval of fecha_emision
                    if (headers.ContainsKey("fecha_emision") && worksheet.Cells[2, headers["fecha_emision"]] != null)
                        fecha = worksheet.Cells[2, headers["fecha_emision"]].Text ?? "";

                    for (int row = 2; row <= rowCount; row++)
                    {
                        var detalle = new Detalle
                        {
                            producto_id = headers.ContainsKey("producto_id") ? worksheet.Cells[row, headers["producto_id"]]?.Text ?? "" : "",
                            cantidad = headers.ContainsKey("cantidad") && double.TryParse(worksheet.Cells[row, headers["cantidad"]]?.Text, out double cantidad) ? cantidad : 0,
                            precio = headers.ContainsKey("precio") && double.TryParse(worksheet.Cells[row, headers["precio"]]?.Text, out double precio) ? precio : 0,
                            porcentaje_iva = headers.ContainsKey("porcentaje_iva") && int.TryParse(worksheet.Cells[row, headers["porcentaje_iva"]]?.Text, out int iva) ? iva : 0,
                            porcentaje_descuento = headers.ContainsKey("porcentaje_descuento") && double.TryParse(worksheet.Cells[row, headers["porcentaje_descuento"]]?.Text, out double descuento) ? descuento : 0,
                            base_cero = headers.ContainsKey("base_cero") && double.TryParse(worksheet.Cells[row, headers["base_cero"]]?.Text, out double baseCero) ? baseCero : 0,
                            base_gravable = 0, // To be assigned below
                            base_no_gravable = headers.ContainsKey("base_no_gravable") && double.TryParse(worksheet.Cells[row, headers["base_no_gravable"]]?.Text, out double baseNoGravable) ? baseNoGravable : 0
                        };

                        // ✅ Corrected base_gravable calculation
                        if (detalle.porcentaje_iva == 0)
                        {
                            detalle.base_cero = detalle.precio * detalle.cantidad;
                            detalle.base_gravable = 0;
                        }
                        else
                        {
                            double descuento_aplicado = (detalle.porcentaje_descuento / 100) * detalle.precio * detalle.cantidad;
                            detalle.base_gravable = (detalle.precio * detalle.cantidad) - descuento_aplicado;
                            detalle.base_cero = 0;
                        }

                        detalles.Add(detalle);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteToFile($"Error reading Excel file: {ex.Message}");
            }
            return detalles;
        }

        // Reads 'pedido' Excel file and extracts client details
        private List<Cliente> ReadExcelDataPedido(string filePath)
        {
            var clientes = new List<Cliente>();
            try
            {
                // ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
                ExcelPackage.License.SetNonCommercialPersonal("My Name");
                using (var package = new ExcelPackage(new FileInfo(filePath)))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    int rowCount = worksheet.Dimension?.Rows ?? 0;
                    int colCount = worksheet.Dimension?.Columns ?? 0;

                    if (rowCount < 2 || colCount == 0) return clientes;

                    var headers = new Dictionary<string, int>();
                    for (int col = 1; col <= colCount; col++)
                    {
                        string header = worksheet.Cells[1, col].Text.Trim().ToLower();
                        if (!string.IsNullOrEmpty(header)) headers[header] = col;
                    }

                    for (int row = 2; row <= rowCount; row++)
                    {
                        var cliente = new Cliente
                        {
                            ruc = headers.ContainsKey("ruc") ? worksheet.Cells[row, headers["ruc"]].Text : "",
                           // fecha_emision = headers.ContainsKey("fecha_emision") ? worksheet.Cells[row, headers["fecha_emision"]].Text : "",
                            cedula = headers.ContainsKey("cedula") ? worksheet.Cells[row, headers["cedula"]].Text : "",
                            razon_social = headers.ContainsKey("razon_social") ? worksheet.Cells[row, headers["razon_social"]].Text : "",
                            telefonos = headers.ContainsKey("telefonos") ? worksheet.Cells[row, headers["telefonos"]].Text : "",
                            direccion = headers.ContainsKey("direccion") ? worksheet.Cells[row, headers["direccion"]].Text : "",
                            tipo = headers.ContainsKey("tipo") ? worksheet.Cells[row, headers["tipo"]].Text : "",
                            email = headers.ContainsKey("email") ? worksheet.Cells[row, headers["email"]].Text : "",
                            es_extranjero = headers.ContainsKey("es_extranjero") && bool.TryParse(worksheet.Cells[row, headers["es_extranjero"]].Text, out bool esExtranjero) ? esExtranjero : false
                        };
                        clientes.Add(cliente);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteToFile($"Error reading Excel file: {ex.Message}");
            }
            return clientes;
        }

        // Creates a document using API call
        private async Task CreateDocumentAsync(List<Detalle> detalles, List<Cliente> pedidos, string detalleFile, string pedidoFile,string fecha)
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Api-Key", apiKey);
                client.DefaultRequestHeaders.Add("Authorization", apiKey);

                var cliente = pedidos.FirstOrDefault();
                if (cliente == null)
                {
                    WriteToFile("❌ No client data available. API call aborted.");
                    return;
                }

            //    string formattedCedula = cliente.cedula.Length == 9 ? "0" + cliente.cedula : cliente.cedula;

                var requestData = new Documento
                {
                    pos = apiToken,
                    fecha_emision = fecha.Replace("-", "/"),
                    tipo_documento = "PRE",
                    estado = "P",
                    caja_id = "",
                    cliente = new Cliente
                    {
                        ruc = cliente.ruc,
                        cedula = cliente.cedula,
                        razon_social = cliente.razon_social,
                        telefonos = cliente.telefonos,
                        direccion = cliente.direccion,
                        tipo = cliente.tipo,
                        email = cliente.email,
                        es_extranjero = cliente.es_extranjero
                    },
                    vendedor = "",
                    descripcion = "DETALLE PREFACTURA",
                    subtotal_0 = detalles.Sum(d => d.base_cero),
                    subtotal_12 = detalles.Sum(d => d.base_gravable),
                    iva = detalles.Sum(d => d.base_gravable * (d.porcentaje_iva / 100.0)),
                    total = detalles.Sum(d => d.base_cero + d.base_gravable + (d.base_gravable * (d.porcentaje_iva / 100.0))),
                    adicional1 = "",
                    detalles = detalles.ToArray()
                };

                string json = JsonConvert.SerializeObject(requestData, Formatting.Indented);
                WriteToFile($"📤 Sending API request:\n{json}");

                HttpResponseMessage response = await client.PostAsync(apiUrl, new StringContent(json, Encoding.UTF8, "application/json"));
                string responseContent = await response.Content.ReadAsStringAsync();

                WriteToFile($"📩 API Response:\n{responseContent}");

                if (response.IsSuccessStatusCode)
                {
                    MoveFileToFolderB(detalleFile);
                    
                    WriteToFile("✅ Files moved to FolderB after successful API response.");
                }
                else
                {
                    WriteToFile("❌ API call failed. Files will not be moved.");
                }
            }
        }


        // Moves file to processed folder
        private void MoveFileToFolderB(string filePath)
        {
            try
            {
                string newFilePath = Path.Combine(processedFolderPath, Path.GetFileName(filePath) + "_old.xlsx");
                if (File.Exists(newFilePath)) File.Delete(newFilePath);
                File.Move(filePath, newFilePath);
                WriteToFile($"📂 Moved file: {Path.GetFileName(filePath)} to FolderB");
            }
            catch (Exception ex)
            {
                WriteToFile($"❌ Error moving file {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }


        private void WriteToFile(string Message)
        {
            try
            {
                string path = AppDomain.CurrentDomain.BaseDirectory + "\\Logs";
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                string filepath = $"{path}\\ServiceLog_{DateTime.Now:yyyy_MM_dd}.txt";
                File.AppendAllText(filepath, DateTime.Now + " - " + Message + Environment.NewLine);
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry("DService", "Error writing to log file: " + ex.Message, EventLogEntryType.Error);
            }
        }
    }
}
