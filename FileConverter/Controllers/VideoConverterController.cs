using FileConverter.Models;
using FileConverter.Services;
using FileConverter.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FileConverter.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VideoConverterController : ControllerBase
    {
        private readonly IJobManager _jobManager;
        private readonly ILogger<VideoConverterController> _logger;

        public VideoConverterController(
            IJobManager jobManager,
            ILogger<VideoConverterController> logger)
        {
            _jobManager = jobManager;
            _logger = logger;
        }

        /// <summary>
        /// Запускает асинхронную конвертацию списка видео в MP3
        /// </summary>
        [HttpPost("to-mp3")]
        public async Task<ActionResult<BatchConversionResponse>> ConvertToMp3(
            [FromBody] VideoConversionRequest request)
        {
            try
            {
                if (request.VideoUrls == null || !request.VideoUrls.Any())
                {
                    return BadRequest("Необходимо указать хотя бы один URL видео для конвертации");
                }

                _logger.LogInformation($"Conversion request received {request.VideoUrls.Count}");
                
                // Получаем результат создания пакета задач
                var jobManagerResult = await _jobManager.EnqueueBatchJobs(request.VideoUrls);
                
                // Создаем объект ответа
                var batchResponse = new BatchConversionResponse
                {
                    // Используем ID пакета из первой задачи (все задачи в пакете имеют один BatchId)
                    BatchId = jobManagerResult.BatchId,
                    Jobs = jobManagerResult.Jobs
                };
                
                batchResponse.BatchStatusUrl = $"{Request.Scheme}://{Request.Host}/api/videoconverter/batch-status/{batchResponse.BatchId}";
                
                return Ok(batchResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while setting video conversion task");
                return StatusCode(500, "Произошла ошибка при конвертации видео. Подробности в логах сервера.");
            }
        }
        
        /// <summary>
        /// Получает текущий статус задачи конвертации видео
        /// </summary>
        [HttpGet("status/{jobId}")]
        public async Task<ActionResult<JobStatusResponse>> GetJobStatus(string jobId)
        {
            try
            {
                var status = await _jobManager.GetJobStatus(jobId);
                return Ok(status);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Задача с ID {jobId} не найдена");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при получении статуса задачи {jobId}");
                return StatusCode(500, "Произошла ошибка при получении статуса задачи");
            }
        }
        
        /// <summary>
        /// Получает статус всех задач в пакете
        /// </summary>
        [HttpGet("batch-status/{batchId}")]
        public async Task<ActionResult<List<JobStatusResponse>>> GetBatchStatus(string batchId)
        {
            try
            {
                var statuses = await _jobManager.GetBatchStatus(batchId);
                return Ok(statuses);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Пакет задач с ID {batchId} не найден");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при получении статуса пакета задач {batchId}");
                return StatusCode(500, "Произошла ошибка при получении статуса пакета задач");
            }
        }
        
        /// <summary>
        /// Получает список всех активных задач конвертации
        /// </summary>
        [HttpGet("jobs")]
        public async Task<ActionResult<List<JobStatusResponse>>> GetAllJobs(
            [FromQuery] int skip = 0, 
            [FromQuery] int take = 20)
        {
            try
            {
                var jobs = await _jobManager.GetAllJobs(skip, take);
                return Ok(jobs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при получении списка задач");
                return StatusCode(500, "Произошла ошибка при получении списка задач");
            }
        }
    }
} 