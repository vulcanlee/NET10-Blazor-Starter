using MyProject.Share.Helpers;

namespace MyProject.Models.Systems;

//
// 摘要:
//     Defines the members of the query.
//
// 備註:
//     DataManagerRequest is used to model bind posted data at server side.
public class DataRequest
{
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = MagicObjectHelper.PageSize;
    //     Specifies the records to skip.
    int Skip { get; set; }

    //     Specifies the records to take.
    public int Take { get; set; }

    //     Sepcifies that the count is required in response.
    public bool RequiresCounts { get; set; }

    //     Specifies the search criteria.
    public string Search { get; set; }
}
