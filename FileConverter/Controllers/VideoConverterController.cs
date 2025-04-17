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
        public async Task<ActionResult<BatchStatusResponse>> GetBatchStatus(string batchId)
        {
            try
            {
                // Получаем статусы отдельных задач из менеджера
                var jobStatuses = await _jobManager.GetBatchStatus(batchId);
                
                // Определяем общий статус пакета задач
                BatchStatus batchStatus = DetermineBatchStatus(jobStatuses);
                
                // Вычисляем общий прогресс
                double overallProgress = jobStatuses.Count > 0 
                    ? jobStatuses.Average(job => job.Progress) 
                    : 0;
                
                // Создаем ответ
                var response = new BatchStatusResponse
                {
                    BatchId = batchId,
                    Status = batchStatus,
                    Jobs = jobStatuses,
                    Progress = overallProgress
                };
                
                return Ok(response);
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
        /// Определяет общий статус пакета задач на основе статусов отдельных задач
        /// </summary>
        private BatchStatus DetermineBatchStatus(List<JobStatusResponse> jobStatuses)
        {
            if (!jobStatuses.Any())
            {
                return BatchStatus.Pending;
            }
            
            // Если все задачи завершились с ошибкой, то пакет Failed
            if (jobStatuses.All(job => job.Status == ConversionStatus.Failed))
            {
                return BatchStatus.Failed;
            }
            
            // Если есть хотя бы одна задача, не завершившая обработку, то пакет в статусе Pending
            bool hasNonCompletedJob = jobStatuses.Any(job => 
                job.Status != ConversionStatus.Completed && 
                job.Status != ConversionStatus.Failed);
                
            if (hasNonCompletedJob)
            {
                return BatchStatus.Pending;
            }
            
            // В остальных случаях (все задачи или завершены успешно, или с ошибкой) - Completed
            return BatchStatus.Completed;
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