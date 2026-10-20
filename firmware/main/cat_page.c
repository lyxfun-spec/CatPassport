#include "cat_page.h"

#include <stddef.h>

static int32_t clamp_i32(int32_t value, int32_t low, int32_t high)
{
    if (value < low) return low;
    if (value > high) return high;
    return value;
}

void cat_page_reset(cat_page_view_t *view)
{
    if (view == NULL) return;
    *view = (cat_page_view_t){0};
    view->page_count = 1;
}

void cat_page_set_geometry(cat_page_view_t *view, int32_t line_height, int32_t content_height,
                           int32_t viewport_height)
{
    if (view == NULL) return;
    if (line_height <= 0 || viewport_height <= 0 || content_height <= 0) {
        cat_page_reset(view);
        return;
    }
    view->line_height = line_height;
    const int32_t lines = viewport_height / line_height;
    view->page_step = (lines > 1 ? lines - 1 : 1) * line_height;

    /* 末页偏移向下夹到行网格，最后一页因此总是整行开头、也不会露出半行。 */
    const int32_t max_y = content_height > viewport_height
                              ? (content_height - viewport_height) / line_height * line_height
                              : 0;
    view->tail_y = max_y;
    view->page_count = (max_y + view->page_step - 1) / view->page_step + 1;
    view->page = clamp_i32(view->page, 0, view->page_count - 1);
}

void cat_page_set_y(cat_page_view_t *view, int32_t y)
{
    if (view == NULL || view->page_step <= 0) return;
    const int32_t bounded = clamp_i32(y, 0, view->tail_y);
    view->page = bounded == view->tail_y ? view->page_count - 1
                                         : clamp_i32(bounded / view->page_step, 0,
                                                     view->page_count - 1);
}

void cat_page_move_by(cat_page_view_t *view, int delta)
{
    if (view == NULL) return;
    view->page = clamp_i32(view->page + delta, 0, view->page_count - 1);
}

void cat_page_last(cat_page_view_t *view)
{
    if (view == NULL) return;
    view->page = view->page_count - 1;
}

int32_t cat_page_y(const cat_page_view_t *view)
{
    if (view == NULL || view->page_step <= 0) return 0;
    const int32_t stepped = view->page * view->page_step;
    return stepped < view->tail_y ? stepped : view->tail_y;
}
