using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accounting.Filters;

public class DateRangeValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        DateTime? fromDate = null;
        DateTime? toDate = null;

        if (context.ActionArguments.TryGetValue("fromDate", out var from) && from is DateTime f)
            fromDate = f;
        if (context.ActionArguments.TryGetValue("toDate", out var to) && to is DateTime t)
            toDate = t;

        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
        {
            context.Result = new BadRequestObjectResult(new { Success = false, Message = "fromDate ต้องไม่มากกว่า toDate" });
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
